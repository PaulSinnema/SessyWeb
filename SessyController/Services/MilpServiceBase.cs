using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Enums;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyController.Services.Optimization.Strategies;
using SessyController.Services.Statistics;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services
{
    public abstract class MilpServiceBase : IMilpService
    {
        // ── Injected services ────────────────────────────────────────────────

        protected readonly LoggingService<MilpServiceBase> _logger;
        private readonly IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;
        protected readonly BatteryContainer _batteryContainer;
        public readonly TimeZoneService _timeZoneService;
        private readonly TaxesDataService _taxesDataService;
        private readonly PlannedActionDataService _plannedActionDataService;
        private readonly PlannedQuarterDataService _plannedQuarterDataService;
        private readonly ForecastSnapshotDataService _forecastSnapshotDataService;
        protected readonly ChargeCostBasisService _chargeCostBasisService;
        protected readonly ReplacementCostService _replacementCostService;
        protected readonly ThrottleAnalysisService _throttleAnalysisService;
        protected readonly WeatherService _weatherService;
        private readonly ControlModeService _controlMode;

        protected SettingsService _settingsService;
        protected Settings _settingsConfig;
        protected SessyBatteryConfig _sessyBatteryConfig;
        protected IDisposable? _sessyBatteryConfigSubscription;
        private string? _configChangedReason;

        // ── Plan state ───────────────────────────────────────────────────────

        protected List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();

        /// <summary>
        /// Measured CC/CV taper of the charge power. It shapes how much energy the plan expects to
        /// arrive per quarter — never how much power is asked for, which is what made the fit
        /// measure its own request.
        /// </summary>
        protected ChargeTaper _chargeTaper = ChargeTaper.None;

        /// <summary>
        /// Outside temperature and 48-hour mean per quarter, as handed to the planner, so the
        /// taper can be evaluated later with the same numbers that capped the solver.
        /// </summary>
        protected Dictionary<DateTime, (double TemperatureC, double Mean48hC)> _taperInputsByTime = new();
        // Battery SOC (Wh) at the end of each quarter, taken directly from the solver so
        // the displayed SOC matches the plan exactly (single source of truth).
        private Dictionary<DateTime, double> _planSocWhByTime = new();

        /// <summary>The planner's reserve floor per quarter (Wh) from the last accepted solve.</summary>
        private Dictionary<DateTime, double> _planFloorWhByTime = new();
        private Dictionary<DateTime, double> _plannedSocByQuarter = new();

        // SOC the current plan started from, and the quarter it starts at. The horizon begins at
        // the current quarter, so that quarter has no predecessor in _plannedSocByQuarter.
        private double _planStartSocWh;
        private DateTime _planStartQuarter = DateTime.MinValue;
        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _minSocWhByTime = new();
        private Dictionary<DateTime, double> _maxSocWhByTime = new();

        /// <summary>Below this many Wh a (dis)charge is not worth issuing; guards drop to ZeroNetHome.</summary>
        private const double MinimumUsefulWh = 25.0;

        /// <summary>
        /// The same floor as a fraction of the bank. 25 Wh was picked for a 16.2 kWh bank; on a
        /// single 5 kWh battery it is inside the resolution of the reported state of charge, so the
        /// guard below fires and releases on measurement noise alone.
        /// </summary>
        private const double MinimumUsefulCapacityRatio = 0.005;

        /// <summary>
        /// How much wider the release threshold is than the stop threshold. Without it the guards
        /// stop and resume on the same bare comparison: ZeroNetHome lets the house drain the
        /// battery, the number climbs back over the threshold, and the mode flips again on the next
        /// cycle. ZeroNetHome is the Sessy strategy NOM and everything else is API, so every flip
        /// is a strategy rewrite.
        /// </summary>
        private const double GuardReleaseFactor = 4.0;

        // Guard state has to survive between cycles, otherwise the hysteresis above is a no-op.
        private bool _chargeRoomGuardHeld;
        private bool _chargeTargetGuardHeld;
        private bool _dischargeGuardHeld;

        private string? _lastRebuildReason;
        private DateTime? _lastBuildTime;
        private double _lastPlanObjectiveEur;
        private int _lastPlanQuarterCount;
        private DateTime? _lastSpeculativeSolveQuarter;
        private HashSet<DateTime> _lastKnownPriceTimes = new();
        private long? _lastPriceSignature;

        // ── Constants ────────────────────────────────────────────────────────

#if DEBUG
        private const int MilpTimeLimitMs = 5000;
#else
        private const int MilpTimeLimitMs = 10000;
#endif
        private const double SocDeviationThresholdPct = 20.0;

        /// <summary>Below this a setpoint correction is noise, not news — see the logging in GetExecutableActionAsync.</summary>
        private const double MaterialPowerChangeW = 250.0;

        private DateTime _lastPowerLogQuarter = DateTime.MinValue;
        private double _lastPowerLogW;

        // PowerW is what the plan expects to flow (taper included, drives the SOC path);
        // RequestedPowerW is what the batteries are asked for on the charge side. 0 means
        // "not known" — a restored or overridden plan — and then PowerW is asked for.
        internal sealed record PlanAction { public Modes Mode; public double PowerW; public double RequestedPowerW; }

        // ── Constructor ──────────────────────────────────────────────────────

        protected MilpServiceBase(
            LoggingService<MilpServiceBase> logger,
            SettingsService settingsService,
            IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
            BatteryContainer batteryContainer,
            TimeZoneService timeZoneService,
            TaxesDataService taxesDataService,
            PlannedActionDataService plannedActionDataService,
            PlannedQuarterDataService plannedQuarterDataService,
            ForecastSnapshotDataService forecastSnapshotDataService,
            ChargeCostBasisService chargeCostBasisService,
            ReplacementCostService replacementCostService,
            ThrottleAnalysisService throttleAnalysisService,
            WeatherService weatherService,
            ControlModeService controlMode)
        {
            _logger = logger;
            _controlMode = controlMode;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
            _taxesDataService = taxesDataService;
            _plannedActionDataService = plannedActionDataService;
            _plannedQuarterDataService = plannedQuarterDataService;
            _forecastSnapshotDataService = forecastSnapshotDataService;
            _chargeCostBasisService = chargeCostBasisService;
            _replacementCostService = replacementCostService;
            _throttleAnalysisService = throttleAnalysisService;
            _weatherService = weatherService;
            _settingsService = settingsService;
            _settingsConfig = settingsService.Current;
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue
                ?? throw new InvalidOperationException("Sessy:Batteries missing");

            _settingsService.SettingsChanged += (s, isStartup) =>
            {
                _settingsConfig = s;
                if (!isStartup)
                {
                    // Window and percentile live in these settings, so the cached value is stale.
                    _replacementCostService.Invalidate();
                    _configChangedReason = "Management settings changed";
                    _logger.LogInformation("MilpService: settings changed — rebuild scheduled.");
                }
            };
            _sessyBatteryConfigSubscription = _sessyBatteryConfigMonitor.OnChange(s =>
            {
                _sessyBatteryConfig = s;
                _configChangedReason = "SessyBatteryConfig changed";
                _logger.LogInformation("MilpService: battery config changed — rebuild scheduled.");
            });
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Main entry point called by BatteriesService each cycle.
        /// </summary>
        public async Task BuildPlanAsync(List<QuarterlyInfo> quarterlyInfos, double currentSocWh)
        {
            _quarterlyInfos = quarterlyInfos;

            await BuildContextAsync().ConfigureAwait(false);

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();
            bool rebuilt = await RebuildIfNeededAsync(nowQuarter, currentSocWh).ConfigureAwait(false);

            WritePlanIntoQuarterlyInfos();
            await WriteBackSocSimulationAsync(currentSocWh).ConfigureAwait(false);
            await ProjectCostBasisAsync(currentSocWh).ConfigureAwait(false);

            if (rebuilt)
            {
                _plannedSocByQuarter = _quarterlyInfos
                    .Where(qi => qi.ChargeLeftWh > 0.0)
                    .ToDictionary(qi => qi.Time, qi => qi.ChargeLeftWh);

                await SavePlanAsync(_lastRebuildReason!).ConfigureAwait(false);

                var planStart = _timeZoneService.Now.DateFloorQuarter();
                var plannedQuarters = _quarterlyInfos
                    .Where(qi => qi.Time >= planStart)
                    .Select(qi => new PlannedQuarter
                    {
                        Time = qi.Time,
                        PlannedMode = qi.Mode.ToString(),
                        PlannedPowerW = qi.PlannedChargePowerW > 0 ? qi.PlannedChargePowerW : -qi.PlannedDischargePowerW,
                        // Split power fields, stored exactly as the planner delivered them so the
                        // view reads direction directly instead of reconstructing it from PlannedMode.
                        PlannedChargePowerW = qi.PlannedChargePowerW,
                        PlannedDischargePowerW = qi.PlannedDischargePowerW,
                        PlannedUnthrottledPowerW = qi.PlannedChargePowerW > 0
                            ? qi.PlannedUnthrottledPowerW
                            : -qi.PlannedUnthrottledPowerW,
                        PlannedChargeLeftWh = qi.ChargeLeftWh,
                        SellingPriceEurKWh = qi.SellingPrice,
                        BuyingPriceEurKWh = qi.BuyingPrice,
                        ProjectedCostBasisEurKWh = qi.ProjectedCostBasisEur,
                        SolarForecastW = qi.SolarPowerPerQuarterInWatts,
                        ConsumptionForecastW = qi.EstimatedConsumptionPerQuarterInWatts,
                        NetLoadWh = qi.NetLoadWh,
                        MinSocWh = _minSocWhByTime.TryGetValue(qi.Time, out var minSoc) ? minSoc : 0.0
                    }).ToList();

                if (plannedQuarters.Count > 0)
                {
                    // One lookup for the whole horizon instead of a query per quarter: with ~288
                    // quarters that was 288 table scans inside a single write transaction.
                    var windowFrom = plannedQuarters.Min(q => q.Time);
                    var windowTo = plannedQuarters.Max(q => q.Time);

                    await _plannedQuarterDataService.AddOrUpdate(
                        plannedQuarters,
                        PlannedQuarterDataService.MatchOn(
                            q => q.Time,
                            q => q.Time >= windowFrom && q.Time <= windowTo)).ConfigureAwait(false);
                }

                await SaveForecastSnapshotsAsync(planStart).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Records the current forecast for each quarter at its lead-time bucket, but only where
        /// that bucket is still empty: the point is to keep the forecast as it looked N hours
        /// ahead, so the first write at a distance is the one that must survive. PlannedQuarter
        /// cannot serve this purpose — it is upserted on every rebuild.
        /// </summary>
        private async Task SaveForecastSnapshotsAsync(DateTime planStart)
        {
            try
            {
                var horizonEnd = _quarterlyInfos.Count > 0 ? _quarterlyInfos.Max(qi => qi.Time) : planStart;

                var existing = await _forecastSnapshotDataService.GetList(async set =>
                    await Task.FromResult(set
                        .Where(f => f.Time >= planStart && f.Time <= horizonEnd)
                        .ToList())).ConfigureAwait(false);

                var known = existing
                    .Select(e => (e.Time, e.LeadHours))
                    .ToHashSet();

                var newRows = new List<ForecastSnapshot>();

                foreach (var qi in _quarterlyInfos.Where(qi => qi.Time >= planStart))
                {
                    int bucket = ForecastSnapshot.BucketFor((qi.Time - planStart).TotalHours);
                    if (!known.Add((qi.Time, bucket))) continue;

                    newRows.Add(new ForecastSnapshot
                    {
                        Time = qi.Time,
                        LeadHours = bucket,
                        SolarForecastW = qi.SolarPowerPerQuarterInWatts,
                        ConsumptionForecastW = qi.EstimatedConsumptionPerQuarterInWatts,
                        CreatedAt = planStart
                    });
                }

                if (newRows.Count > 0)
                    await _forecastSnapshotDataService.Add(newRows).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Learning data is nice to have; never let it break a plan write.
                _logger.LogWarning($"Storing forecast snapshots failed: {ex.Message}");
            }
        }

        public async Task<(Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter)
        {
            var action = await GetExecutableActionAsync(nowQuarter).ConfigureAwait(false);
            return (action.Mode, action.PowerW);
        }

        public bool HasPlanFor(DateTime quarter) => _planByTime.ContainsKey(quarter);

        // Reuses the settings-changed path: RebuildIfNeededAsync treats a pending reason as a
        // forced rebuild and clears it after one use.
        public void ForceRebuild(string reason) => _configChangedReason = reason;

        public async Task ClearPlanAsync()
        {
            _planByTime.Clear();
            _lastPriceSignature = null;
            _lastBuildTime = null;
            _lastPlanObjectiveEur = 0.0;
            _lastPlanQuarterCount = 0;
            _planStartQuarter = DateTime.MinValue;
            _planStartSocWh = 0.0;
            _lastSpeculativeSolveQuarter = null;
            _lastKnownPriceTimes = new();
            await _plannedActionDataService.ClearPlanAsync().ConfigureAwait(false);
            _logger.LogInformation("Plan cleared.");
        }

        public async Task<bool> TryRestorePlanAsync()
        {
            try
            {
                var actions = await _plannedActionDataService.LoadPlanAsync().ConfigureAwait(false);
                if (!actions.Any()) return false;

                var restored = new Dictionary<DateTime, PlanAction>();
                foreach (var a in actions)
                    if (Enum.TryParse<Modes>(a.Mode, out var mode))
                        restored[a.Time] = new PlanAction { Mode = mode, PowerW = a.PowerW };

                _planByTime = restored;
                _plannedSocByQuarter = actions
                    .Where(a => a.ChargeLeftWh > 0.0)
                    .ToDictionary(a => a.Time, a => a.ChargeLeftWh);
                _lastPlanObjectiveEur = actions.First().ObjectiveEur;
                _lastPlanQuarterCount = restored.Count;
                _lastPriceSignature = actions.First().PriceSignature;
                _lastKnownPriceTimes = new();

                _logger.LogInformation($"Plan restored: {restored.Count} quarters.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not restore plan: {ex.Message}");
                return false;
            }
        }

        public async Task<PlanStatistics> GetPlanStatisticsAsync(DateTime now, double currentSocWh)
        {
            var futurePlan = _planByTime.Where(kvp => kvp.Key >= now).OrderBy(kvp => kvp.Key).ToList();
            var history = await _plannedActionDataService.GetPlanHistoryAsync(20).ConfigureAwait(false);

            return new PlanStatistics
            {
                LastBuildTime = _lastBuildTime,
                IsRestoredFromDb = _lastBuildTime == null,
                PlanHorizon = futurePlan.Any() ? futurePlan.Max(kvp => kvp.Key) : (DateTime?)null,
                TotalFutureQuarters = futurePlan.Count,
                ChargingQuarters = futurePlan.Count(kvp => kvp.Value.Mode == Modes.Charging),
                DischargingQuarters = futurePlan.Count(kvp => kvp.Value.Mode == Modes.Discharging),
                NzhQuarters = futurePlan.Count(kvp => kvp.Value.Mode == Modes.ZeroNetHome),
                ExpectedProfitEur = _lastPlanObjectiveEur,
                NextDischargeTime = futurePlan.FirstOrDefault(kvp => kvp.Value.Mode == Modes.Discharging).Key is var dt && dt == default ? null : dt,
                NextChargeTime = futurePlan.FirstOrDefault(kvp => kvp.Value.Mode == Modes.Charging).Key is var ct && ct == default ? null : ct,
                SocDeviationPct = GetCurrentSocDeviationPct(now, currentSocWh),
                NightReservePct = MinimumReserveRatio * 100.0,
                NightReserveWh = MinimumReserveRatio * _batteryContainer.GetTotalCapacity(),
                NightReserveIsLearned = false,
                RecentHistory = history,
            };
        }

        /// <summary>Minimum reserve as a fraction of capacity: the one floor the planner keeps (default 0).</summary>
        private double MinimumReserveRatio => Math.Clamp(_settingsConfig.FixedNightReservePct, 0.0, 100.0) / 100.0;

        /// <summary>
        /// Deviation between live SOC and the SOC the plan expects at this moment.
        /// _plannedSocByQuarter holds END-of-quarter SOC, so interpolate from the previous
        /// quarter's end value by elapsed fraction; comparing directly reports a full quarter
        /// of (dis)charge as deviation and triggers phantom forced replans.
        /// </summary>
        public double GetCurrentSocDeviationPct(DateTime now, double currentSocWh)
        {
            double capacityWh = _batteryContainer.GetTotalCapacity();
            if (capacityWh <= 0) return 0.0;

            var nowQuarter = now.DateFloorQuarter();
            if (!_plannedSocByQuarter.TryGetValue(nowQuarter, out double socEndWh))
                return 0.0;

            // Start of this quarter = end of the previous one; for the first quarter of the plan
            // that is the SOC the solve started from.
            double socStartWh;
            if (_plannedSocByQuarter.TryGetValue(nowQuarter.AddMinutes(-15), out double prevEndWh))
                socStartWh = prevEndWh;
            else if (nowQuarter == _planStartQuarter)
                socStartWh = _planStartSocWh;
            else
                socStartWh = socEndWh;

            double elapsedFraction = (now - nowQuarter).TotalMinutes / 15.0;
            elapsedFraction = Math.Clamp(elapsedFraction, 0.0, 1.0);

            double expectedSocWh = socStartWh + (socEndWh - socStartWh) * elapsedFraction;

            return Math.Abs(currentSocWh - expectedSocWh) / capacityWh * 100.0;
        }

        public void Dispose() => _sessyBatteryConfigSubscription?.Dispose();

        // ── Context builder ──────────────────────────────────────────────────

        /// <summary>
        /// Builds per-quarter context:
        ///   _nettingByTime   — netting flag from Taxes
        ///   _minSocWhByTime  — minimum SOC (minimum reserve setting)
        ///   _maxSocWhByTime  — maximum SOC (solar headroom)
        /// </summary>
        private async Task BuildContextAsync()
        {
            _nettingByTime.Clear();
            _minSocWhByTime.Clear();
            _maxSocWhByTime.Clear();

            double capWh = _batteryContainer.GetTotalCapacity();
            double capKWh = capWh / 1000.0;

            var ordered = _quarterlyInfos.OrderBy(q => q.Time).ToList();

            // Load netting flags
            var taxesCacheByDate = new Dictionary<DateTime, Taxes?>();
            foreach (var qi in ordered)
            {
                var day = qi.Time.Date;
                if (!taxesCacheByDate.TryGetValue(day, out var taxes))
                {
                    taxes = await _taxesDataService.GetTaxesForDate(qi.Time).ConfigureAwait(false);
                    taxesCacheByDate[day] = taxes;
                }
                _nettingByTime[qi.Time] = taxes?.Netting ?? true;
            }

            // Plain floor: the planner prices the night itself and the BMS protects the cells.
            double minimumReserveWh = capWh * MinimumReserveRatio;

            foreach (var qi in ordered)
            {
                // maxSoc = full capacity. The grid-balance solver decides for itself
                // whether to store solar surplus or export it, so no artificial headroom
                // is needed — that previously forced pointless early dumping.
                _minSocWhByTime[qi.Time] = minimumReserveWh;
                _maxSocWhByTime[qi.Time] = capWh;
            }
        }

        // ── Rebuild logic ────────────────────────────────────────────────────

        private async Task<bool> RebuildIfNeededAsync(DateTime nowQuarter, double currentSocWh)
        {
            var currentKnownTimes = _quarterlyInfos
                .Where(x => !x.IsPriceExpected)
                .Select(x => x.Time)
                .ToHashSet();

            bool newPricesArrived = currentKnownTimes.Any(t => !_lastKnownPriceTimes.Contains(t));

            bool forced = false;
            bool reserveTriggered = false;
            string? reason = null;

            if (_planByTime.Count == 0)
            {
                reason = "No plan exists"; forced = true;
            }
            else if (_configChangedReason != null)
            {
                reason = _configChangedReason; _configChangedReason = null; forced = true;
            }
            else if (_lastKnownPriceTimes.Count == 0)
            {
                reason = "First run or after restore"; forced = true;
            }
            else if (newPricesArrived)
            {
                int newCount = currentKnownTimes.Count(t => !_lastKnownPriceTimes.Contains(t));
                reason = $"New EPEX prices arrived ({newCount} quarters)"; forced = true;
            }
            else if (_controlMode.WeMayDriveTheBatteries
                     && Math.Abs(GetCurrentSocDeviationPct(_timeZoneService.Now, currentSocWh)) > SocDeviationThresholdPct)
            {
                // Only news while we are driving. Under Charged the SOC walks away from our plan
                // by design — that is not a deviation to correct, it is someone else steering, and
                // forcing a rebuild on it re-anchors the plan and writes a PlannedActions row every
                // time the gap reopens. The plan stays fresh on the quarterly speculative solve.
                reason = $"SOC deviation exceeded {SocDeviationThresholdPct}%"; forced = true;
            }
            else if (_controlMode.WeMayDriveTheBatteries
                     && _reserveRebuildQuarter != nowQuarter
                     && ReserveReachedUnderCover(
                            _planByTime.TryGetValue(nowQuarter, out var nowAct) ? nowAct.Mode : Modes.Unknown,
                            currentSocWh,
                            _planFloorWhByTime.TryGetValue(nowQuarter, out var floorWh) ? floorWh : 0.0))
            {
                // House used more than forecast: ZNH would cover it below the reserve until the
                // next quarter. The planner re-decides from the measured SOC (→ HoldReserve).
                // The floor is the planner's own (ReserveFloor), so both sides agree on "at reserve".
                reserveTriggered = true;
                reason = "SOC at reserve while the plan covers the house"; forced = true;
            }
            else if (_lastSpeculativeSolveQuarter != nowQuarter)
            {
                reason = "Quarterly speculative solve"; forced = false;
            }

            if (reason == null) return false;

            double previousObjective = _lastPlanObjectiveEur;
            int previousQuarterCount = _lastPlanQuarterCount;

            // ApplySolveResult installs the new plan before the accept/reject decision below,
            // so keep a copy: a rejected solve must not stay in control of the batteries.
            var previousPlanByTime = new Dictionary<DateTime, PlanAction>(_planByTime);
            var previousPlanSocWhByTime = new Dictionary<DateTime, double>(_planSocWhByTime);
            var previousPlanFloorWhByTime = new Dictionary<DateTime, double>(_planFloorWhByTime);
            var previousPlanStartQuarter = _planStartQuarter;
            double previousPlanStartSocWh = _planStartSocWh;

            _logger.LogInformation($"Solving plan: {reason} (forced={forced})");

            bool built = await BuildMilpPlanAsync(currentSocWh).ConfigureAwait(false);

            if (!forced) _lastSpeculativeSolveQuarter = nowQuarter;

            if (!built)
            {
                _logger.LogWarning("MILP solve failed — keeping previous plan.");
                return false;
            }

            // Once per quarter, and only after a solve that went through: should the planner keep
            // ZNH, rebuilding again will not change it; a failed solve may retry next cycle.
            if (reserveTriggered)
                _reserveRebuildQuarter = nowQuarter;

            // Compare EUR/quarter, not the raw total: the horizon shrinks as the day progresses,
            // so a later solve's total is smaller even when it is strictly better.
            if (!forced && previousQuarterCount > 0 && _lastPlanQuarterCount > 0)
            {
                double previousRate = previousObjective / previousQuarterCount;
                double newRate = _lastPlanObjectiveEur / _lastPlanQuarterCount;

                if (previousRate > 0 && newRate <= previousRate)
                {
                    _logger.LogWarning(
                        $"Speculative solve rejected: {newRate:F4} <= {previousRate:F4} EUR/quarter " +
                        $"({_lastPlanObjectiveEur:F4}/{_lastPlanQuarterCount} <= {previousObjective:F4}/{previousQuarterCount}).");

                    _planByTime = previousPlanByTime;
                    _planSocWhByTime = previousPlanSocWhByTime;
                    _planFloorWhByTime = previousPlanFloorWhByTime;
                    _lastPlanObjectiveEur = previousObjective;
                    _lastPlanQuarterCount = previousQuarterCount;
                    _planStartQuarter = previousPlanStartQuarter;
                    _planStartSocWh = previousPlanStartSocWh;

                    // The solve also wrote its modes, powers and SOC path onto _quarterlyInfos.
                    // Re-derive those from the restored plan, otherwise the runtime guards and the
                    // UI keep reading values belonging to the plan we just threw away.
                    await WriteBackSocSimulationAsync(currentSocWh).ConfigureAwait(false);

                    return false;
                }
            }

            if (!forced)
                reason = $"Quarterly speculative solve accepted ({previousObjective:F4} → {_lastPlanObjectiveEur:F4} EUR)";

            _lastKnownPriceTimes = currentKnownTimes;
            _lastPriceSignature = CalculatePriceSignature(_quarterlyInfos);
            _lastBuildTime = _timeZoneService.Now;
            _lastRebuildReason = reason;
            return true;
        }

        /// <summary>Same slack as the planner's reserve check (5 Wh), so a rebuild cannot loop.</summary>
        private const double ReserveToleranceWh = 5.0;

        /// <summary>Quarter of the last reserve-triggered rebuild, so it fires at most once per quarter.</summary>
        private DateTime? _reserveRebuildQuarter;

        /// <summary>
        /// Measured SOC has reached the planner's floor while the plan still says ZeroNetHome.
        /// The planner turns such a quarter into HoldReserve, so after one rebuild this is false.
        /// </summary>
        internal static bool ReserveReachedUnderCover(Modes plannedMode, double socWh, double floorWh)
            => plannedMode == Modes.ZeroNetHome
               && floorWh > 0.0
               && socWh <= floorWh + ReserveToleranceWh;

        /// <summary>
        /// Superseded by the planner's own ReserveFloor (_planFloorWhByTime); kept for reference.
        /// The reserve the planner holds from now on: the highest one still to come within its
        /// horizon (BatteryGreedyPlanner.MinSocFrom), e.g. the bridge reserve. This quarter's
        /// own reserve alone can be lower, which made the trigger fire too late.
        /// </summary>
        internal static double PlannerFloorWh(IReadOnlyDictionary<DateTime, double> minSocWhByTime, DateTime nowQuarter, DateTime horizonEnd)
        {
            double floor = 0.0;
            foreach (var kv in minSocWhByTime)
                if (kv.Key >= nowQuarter && kv.Key < horizonEnd && kv.Value > floor)
                    floor = kv.Value;
            return floor;
        }

        private static long CalculatePriceSignature(List<QuarterlyInfo> infos)
        {
            unchecked
            {
                long hash = 0;
                foreach (var q in infos.Where(x => !x.IsPriceExpected).OrderBy(x => x.Time))
                {
                    hash = hash * 31 + q.Time.Ticks;
                    hash = hash * 31 + (long)(q.BuyingPrice * 100000);
                }
                return hash;
            }
        }

        // ── MILP solve ───────────────────────────────────────────────────────

        protected abstract Task<bool> BuildMilpPlanAsync(double socWh);


        // ── Shared helpers for concrete strategy implementations ─────────────

        /// <summary>
        /// Builds SOC bounds per quarter — shared by all strategies.
        /// maxSoc is full capacity. It used to be reduced by that quarter's solar surplus to keep
        /// an old solver feasible when the battery filled up while solar was producing; the greedy
        /// planner exports whatever it cannot absorb, so it can never be infeasible. The reduction
        /// only capped every plan at capacity − surplus, which is why a plan never targeted 100%.
        /// </summary>
        protected List<SocBound> BuildSocBounds(List<QuarterlyInfo> quarters, double socKWh, double capKWh)
        {
            return quarters.Select(q =>
            {
                double mn = _minSocWhByTime.TryGetValue(q.Time, out var minV) ? minV / 1000.0 : 0.0;
                double mx = _maxSocWhByTime.TryGetValue(q.Time, out var maxV) ? maxV / 1000.0 : capKWh;

                mn = Math.Max(0.0, Math.Min(mn, capKWh));
                mx = Math.Max(Math.Min(mx, capKWh), mn + 0.01);

                if (mn > socKWh) mn = socKWh;

                return new SocBound(q.Time, mn, mx);
            }).ToList();
        }

        /// <summary>
        /// Applies a solve result into _planByTime and returns success.
        /// Shared by all strategy implementations.
        /// </summary>
        protected bool ApplySolveResult(PlanResult? result, long elapsedMs, int quarterCount, double socKWh)
        {
            if (result == null)
            {
                _logger.LogWarning($"MILP solver returned no result. quarters={quarterCount}, socKWh={socKWh:F2}");
                return false;
            }

            _logger.LogWarning($"MILP solved: optimal={result.Optimal}, obj={result.ObjectiveEur:F4} EUR" +
                $", elapsed={elapsedMs}ms, quarters={quarterCount}");

            var newPlan = new Dictionary<DateTime, PlanAction>();
            var newSoc = new Dictionary<DateTime, double>();
            var newFloor = new Dictionary<DateTime, double>();

            foreach (var p in result.Plan)
            {
                Modes mode;
                double powerW;
                double requestedW = 0.0;

                switch (p.Mode)
                {
                    case ActionMode.Charge:
                        mode = Modes.Charging;
                        powerW = Math.Round(p.ChargeKW * 1000.0, 0);
                        requestedW = Math.Round(p.RequestedChargeKW * 1000.0, 0);
                        break;
                    case ActionMode.Discharge:
                        mode = Modes.Discharging;
                        powerW = Math.Round(p.DischargeKW * 1000.0, 0);
                        requestedW = Math.Round(p.RequestedDischargeKW * 1000.0, 0);
                        break;
                    case ActionMode.HoldReserve:
                        mode = Modes.HoldReserve;
                        powerW = 0.0;
                        break;
                    case ActionMode.Disabled:
                        mode = Modes.Disabled;
                        powerW = 0.0;
                        break;
                    case ActionMode.ZeroNetHome:
                        mode = Modes.ZeroNetHome;
                        powerW = Math.Round(p.DischargeKW * 1000.0, 0);
                        break;
                    default:
                        mode = Modes.ZeroNetHome;
                        powerW = 0.0;
                        break;
                }

                newPlan[p.Start] = new PlanAction { Mode = mode, PowerW = powerW, RequestedPowerW = requestedW };
                newSoc[p.Start] = p.SocEndKWh * 1000.0;
                newFloor[p.Start] = p.ReserveFloorKWh * 1000.0;
            }

            foreach (var qi in _quarterlyInfos)
            {
                if (!newPlan.ContainsKey(qi.Time))
                {
                    newPlan[qi.Time] = _planByTime.TryGetValue(qi.Time, out var existing)
                        ? existing
                        : new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                }
            }

            _planByTime = newPlan;
            _planSocWhByTime = newSoc;
            _planFloorWhByTime = newFloor;
            _lastPlanObjectiveEur = result.ObjectiveEur;
            _lastPlanQuarterCount = quarterCount;
            _planStartQuarter = newSoc.Count > 0 ? newSoc.Keys.Min() : DateTime.MinValue;
            _planStartSocWh = socKWh * 1000.0;   // parameter is kWh, this field is Wh
            return true;
        }

        // ── Plan application ─────────────────────────────────────────────────

        private void WritePlanIntoQuarterlyInfos()
        {
            foreach (var qi in _quarterlyInfos)
            {
                if (!_planByTime.TryGetValue(qi.Time, out var act))
                {
                    qi.SetMode(Modes.ZeroNetHome);
                    qi.SetPlanPower(0, 0);
                    continue;
                }

                qi.SetMode(act.Mode);

                if (act.Mode == Modes.Charging)
                    qi.SetPlanPower(act.PowerW, 0, RequestedChargePowerW(act));
                else if (act.Mode == Modes.Discharging)
                    qi.SetPlanPower(0, act.PowerW, RequestedDischargePowerW(act));
                else
                    // ZeroNetHome still carries a self-consumption discharge from the solve
                    // (ApplySolveResult's ActionMode.ZeroNetHome case) — showing 0 here hid the
                    // very flow that keeps ChargeLeftWh moving while the mode stays ZeroNetHome.
                    qi.SetPlanPower(0, act.PowerW);
            }
        }

        /// <summary>
        /// Charge power to ASK the batteries for. The planner emits it directly: the plan's own
        /// power is what the taper lets through and belongs to the SOC path, not to the setpoint.
        /// Falls back to the plan power when the request is unknown — a restored plan or a runtime
        /// override carries no request.
        ///
        /// This used to be reconstructed as plannedPower / taperRatio, which fed itself: the
        /// batteries were commanded at the tapered value, the fit measured that command against
        /// the reported target, and so it kept measuring its own request. Between 28-07 and 05-08
        /// that drove the requested share of nameplate from 76% down to 53% while the hardware
        /// delivered 100% of every request.
        /// </summary>
        internal static double RequestedChargePowerW(PlanAction act)
            => act.RequestedPowerW > act.PowerW ? act.RequestedPowerW : act.PowerW;

        /// <summary>
        /// Setpoint for a charging quarter: at least the solar surplus, because whatever the
        /// setpoint leaves unabsorbed leaves the house at the midday selling price — the guard this
        /// replaces compared the surplus with a full charge step (1650 Wh) while the roof peaks at
        /// ~900 Wh per quarter, so it could never fire. Never more than <paramref name="limitWh"/>,
        /// which is the smaller of the room left and what the session still wants.
        /// </summary>
        internal static double ChargeSetpointW(double requestedW, double netLoadWh, double limitWh)
        {
            double surplusW = netLoadWh < 0.0 ? -netLoadWh * 4.0 : 0.0;
            return Math.Min(Math.Max(requestedW, surplusW), limitWh * 4.0);
        }

        /// <summary>
        /// As above, but the ceiling is what is still wanted within one command interval, not within a
        /// whole quarter. The ×4 variant (kept above, unused) assumed a full quarter was always left, so
        /// the tail slowed down exponentially and never reached the target.
        /// </summary>
        internal static double ChargeSetpointW(double requestedW, double netLoadWh, double limitWh, double intervalHours)
        {
            double surplusW = netLoadWh < 0.0 ? -netLoadWh * 4.0 : 0.0;
            return Math.Min(Math.Max(requestedW, surplusW), limitWh / Math.Max(intervalHours, 1.0 / 3600.0));
        }

        /// <summary>How long a charge command stands before it is recomputed: one control cycle.</summary>
        private static double CommandIntervalHours => BatteriesService.HeartbeatIntervalSeconds / 3600.0;

        /// <summary>
        /// The energy below which a (dis)charge is not worth issuing, scaled to the bank so that a
        /// single battery gets a deadband it can actually resolve. Never below MinimumUsefulWh.
        /// </summary>
        internal static double MinimumUsefulEnergyWh(double capacityWh)
            => Math.Max(MinimumUsefulWh, capacityWh * MinimumUsefulCapacityRatio);

        /// <summary>
        /// Whether a runtime guard is holding, with hysteresis: it engages at <paramref name="stopWh"/>
        /// and only releases once the available energy has climbed back over <paramref name="releaseWh"/>.
        /// Pure, so the noise case is testable without a battery.
        /// </summary>
        internal static bool GuardHolds(bool wasHolding, double availableWh, double stopWh, double releaseWh)
            => wasHolding ? availableWh < releaseWh : availableWh <= stopWh;

        /// <summary>
        /// Discharge power to ASK the batteries for — the same rule as RequestedChargePowerW, on
        /// the other side. The plan's own power is what the measured capability lets through and
        /// belongs to the SOC path; the setpoint is the request.
        ///
        /// This replaces a reconstruction, plannedPower / throttleRatio, that could never be
        /// right: the batteries were commanded at the capped value and the fit then divided that
        /// same command by the reconstructed target, so for any ratio r the next measurement
        /// returned exactly r. Every value was a fixed point and the discharge throttle sat frozen
        /// at 0.60 while ~0.80 of nameplate was demonstrably available.
        /// </summary>
        internal static double RequestedDischargePowerW(PlanAction act)
            => act.RequestedPowerW > act.PowerW ? act.RequestedPowerW : act.PowerW;

        /// <summary>
        /// How much the current charging session still wants, in Wh: the planned SOC at the end of
        /// the last consecutive charging quarter minus the live SOC. This is the ceiling that lets
        /// the front of a session run at full power — pulling energy forward inside one price block
        /// costs nothing — while the tail can never buy past what the plan asked for.
        /// Falls back to <paramref name="fallbackWh"/> when the plan holds no SOC for the session.
        /// </summary>
        private double SessionRemainingWh(DateTime nowQuarter, double socWh, double fallbackWh)
        {
            var quarter = nowQuarter;
            DateTime? lastChargeQuarter = null;

            while (_planByTime.TryGetValue(quarter, out var act) && act.Mode == Modes.Charging)
            {
                lastChargeQuarter = quarter;
                quarter = quarter.AddMinutes(15);
            }

            if (lastChargeQuarter == null) return fallbackWh;

            // _planSocWhByTime comes from the last solve, _plannedSocByQuarter survives a restart.
            if (!_planSocWhByTime.TryGetValue(lastChargeQuarter.Value, out double targetWh) &&
                !_plannedSocByQuarter.TryGetValue(lastChargeQuarter.Value, out targetWh))
                return fallbackWh;

            return Math.Max(0.0, targetWh - socWh);
        }

        /// <summary>
        /// Projects the FIFO cost basis forward through the plan and stores the average
        /// cost basis per quarter on each QuarterlyInfo. The FIFO bookkeeping itself lives in
        /// ChargeCostBasisService so the measured and the projected branch cannot drift apart;
        /// this only maps the plan onto its input contract.
        ///
        /// The per-quarter SOC from the simulation (ChargeLeftWh) drives it, not the planned
        /// power, because that also captures ZeroNetHome — a quarter that stores solar while
        /// covering the house at the same time.
        /// </summary>
        private async Task ProjectCostBasisAsync(double currentSocWh)
        {
            try
            {
                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                var ordered = _quarterlyInfos
                    .Where(q => q.Time >= nowQuarter)
                    .OrderBy(q => q.Time)
                    .ToList();

                if (ordered.Count == 0) return;

                var steps = ordered
                    .Select(q => new ChargeCostBasisService.ProjectionStep(
                        q.Time,
                        q.ChargeLeftWh,
                        Math.Max(0.0, -q.NetLoadWh),
                        q.BuyingPrice))
                    .ToList();

                var projection = await _chargeCostBasisService
                    .ProjectAsync(steps, currentSocWh).ConfigureAwait(false);

                // ProjectAsync returns one entry per step, in order.
                for (int i = 0; i < ordered.Count && i < projection.Quarters.Count; i++)
                {
                    ordered[i].SetProjectedCostBasis(
                        projection.Quarters[i].AvgStoredEurPerKWh,
                        projection.Quarters[i].AvgDeliveredEurPerKWh);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Cost basis projection failed: {ex.Message}");
            }
        }

        private async Task WriteBackSocSimulationAsync(double soc)
        {
            if (_quarterlyInfos.Count == 0) return;

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();
            double capWh = _batteryContainer.GetTotalCapacity();
            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * BatteryConstants.FullThresholdRatio;

            static double Clamp(double v, double min, double max)
                => v < min ? min : (v > max ? max : v);

            // Re-anchor the displayed SOC forecast on the measured SOC every cycle, not only on a
            // rebuild. The committed plan (_plannedSocByQuarter / _planSocWhByTime) stays the single
            // source of truth for the deviation metric and the rebuild trigger; here we only shape
            // the forward line, applying the solver's per-quarter SOC deltas onto the live measured
            // SOC so the forecast starts where the battery actually is and tracks the plan's intent
            // from there. prevSolverSoc seeds from the solver SOC entering the first projected
            // quarter so the first delta has the right reference.
            double? prevSolverSoc = null;
            if (_planSocWhByTime.TryGetValue(nowQuarter.AddMinutes(-15), out var socBeforeNow))
                prevSolverSoc = socBeforeNow;
            else if (_planStartQuarter == nowQuarter)
                prevSolverSoc = _planStartSocWh;

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time).Where(q => q.Time >= nowQuarter))
            {
                if (!_planByTime.TryGetValue(qi.Time, out var act))
                {
                    act = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    _planByTime[qi.Time] = act;
                }

                double minSocWh = _minSocWhByTime.TryGetValue(qi.Time, out var mn) ? mn : 0.0;
                double maxSocWh = _maxSocWhByTime.TryGetValue(qi.Time, out var mx) ? mx : capWh;
                maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

                double netLoadWh = qi.NetLoadWh;

                // Within the solver horizon, follow the solver's per-quarter SOC delta
                // (re-anchored onto the measured SOC above); beyond it (predicted-price
                // window) simulate the step from the running SOC.
                if (_planSocWhByTime.TryGetValue(qi.Time, out var solverSoc))
                {
                    if (prevSolverSoc.HasValue)
                        soc = Clamp(soc + (solverSoc - prevSolverSoc.Value), 0.0, capWh);
                    else
                        soc = Clamp(solverSoc, 0.0, capWh); // no delta reference — fall back to absolute
                    prevSolverSoc = solverSoc;
                    qi.SetChargeNeeded(act.Mode == Modes.Charging ? maxSocWh : minSocWh);
                }
                else if (act.Mode == Modes.Charging)
                {
                    double chargeWh = act.PowerW > 10 ? act.PowerW * 0.25 : chargeStepWh;
                    soc = Clamp(soc + chargeWh, 0.0, capWh);
                    qi.SetChargeNeeded(maxSocWh);
                }
                else if (act.Mode == Modes.Discharging)
                {
                    double dischargeWh = act.PowerW > 10 ? act.PowerW * 0.25 : dischargeStepWh;
                    soc = Clamp(soc - dischargeWh, 0.0, capWh);
                    qi.SetChargeNeeded(minSocWh);
                }
                else
                {
                    // HoldReserve stores surplus only, never covers a deficit.
                    soc = Clamp(soc - (act.Mode == Modes.HoldReserve ? Math.Min(netLoadWh, 0.0) : netLoadWh), 0.0, capWh);
                    qi.SetChargeNeeded(minSocWh);
                }

                qi.SetChargeLeft(soc);
                qi.SetMode(act.Mode);

                // Pass the unthrottled target along: omitting it resets PlannedUnthrottledPowerW
                // to 0 for every quarter, wiping what WritePlanIntoQuarterlyInfos just set.
                if (act.Mode == Modes.Charging)
                {
                    double chargeW = act.PowerW > 0 ? act.PowerW : _batteryContainer.GetChargingCapacityInWattsPerHour();
                    qi.SetPlanPower(chargeW, 0, Math.Max(chargeW, RequestedChargePowerW(act)));
                }
                else if (act.Mode == Modes.Discharging)
                {
                    double dischargeW = act.PowerW > 0 ? act.PowerW : _batteryContainer.GetDischargingCapacityInWattsPerHour();
                    qi.SetPlanPower(0, dischargeW, Math.Max(dischargeW, RequestedDischargePowerW(act)));
                }
                else
                    // Same as WritePlanIntoQuarterlyInfos: ZeroNetHome carries a self-consumption
                    // discharge from the solve, not a flat 0 — this else runs after that one and
                    // was overwriting it back to 0 on every quarter from now on.
                    qi.SetPlanPower(0, act.PowerW);
            }
        }

        // ── Runtime action ───────────────────────────────────────────────────

        /// <summary>A guard stops the planned flow: hold the energy, store live surplus only.</summary>
        internal static PlanAction HoldAction(QuarterlyInfo? qi)
        {
            qi?.SetMode(Modes.HoldReserve);
            qi?.SetPlanPower(0, 0);
            return new PlanAction { Mode = Modes.HoldReserve, PowerW = 0 };
        }

        private async Task<PlanAction> GetExecutableActionAsync(DateTime nowQuarter)
        {
            if (!_planByTime.TryGetValue(nowQuarter, out var planned))
                return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };

            double capWh = _batteryContainer.GetTotalCapacity();
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * BatteryConstants.FullThresholdRatio;

            double minSocWh = _minSocWhByTime.TryGetValue(nowQuarter, out var mn) ? mn : 0.0;
            double maxSocWh = _maxSocWhByTime.TryGetValue(nowQuarter, out var mx) ? mx : capWh;
            maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

            var qi = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);

            // Stop and release thresholds, scaled to the bank. Both guards below engage at stopWh
            // and only let go above releaseWh; a bare comparison flips them on SOC noise.
            double stopWh = MinimumUsefulEnergyWh(capWh);
            double releaseWh = stopWh * GuardReleaseFactor;

            if (planned.Mode == Modes.Charging)
            {
                double roomWh = maxSocWh - socWh;

                bool roomGuardWasHeld = _chargeRoomGuardHeld;
                _chargeRoomGuardHeld = GuardHolds(roomGuardWasHeld, roomWh, stopWh, releaseWh);

                if (_chargeRoomGuardHeld)
                {
                    // Once per engagement — this method runs on every heartbeat and every UI refresh.
                    if (!roomGuardWasHeld)
                        _logger.LogWarning(
                            $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: GUARD_CHARGE_NO_ROOM → HoldReserve " +
                            $"(socWh={socWh:F0}, maxSocWh={maxSocWh:F0}, roomWh={roomWh:F0}, releaseWh={releaseWh:F0})");

                    // Was ZeroNetHome: covered the house from just-bought energy or below the reserve.
                    return HoldAction(qi);
                }

                // Ask for the plan's REQUEST, not its tapered expectation. The batteries clamp
                // themselves; commanding the tapered value only guaranteed we stayed under their
                // limit and made the taper fit measure its own request.
                double requestedW = RequestedChargePowerW(planned);

                // Two ceilings: the room left in the battery, and what the rest of this charging
                // session still wants. Running the front of a session at full power only pulls
                // energy forward inside one price block, but the tail must not buy past the plan.
                double limitWh = Math.Min(roomWh, SessionRemainingWh(nowQuarter, socWh, roomWh));

                bool targetGuardWasHeld = _chargeTargetGuardHeld;
                _chargeTargetGuardHeld = GuardHolds(targetGuardWasHeld, limitWh, stopWh, releaseWh);

                if (_chargeTargetGuardHeld)
                {
                    if (!targetGuardWasHeld)
                        _logger.LogWarning(
                            $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: GUARD_CHARGE_TARGET_REACHED → HoldReserve " +
                            $"(socWh={socWh:F0}, limitWh={limitWh:F0}, releaseWh={releaseWh:F0})");

                    // Was ZeroNetHome: covered the house from just-bought energy or below the reserve.
                    return HoldAction(qi);
                }

                double chargeW = ChargeSetpointW(requestedW, qi?.NetLoadWh ?? 0.0, limitWh, CommandIntervalHours);

                // Only report a correction that is worth reporting, and only once per quarter:
                // this method runs on every cycle and every UI refresh, so an unconditional line
                // buries the log in dozens of copies of the same 1% adjustment.
                if (Math.Abs(chargeW - requestedW) >= MaterialPowerChangeW &&
                    (_lastPowerLogQuarter != nowQuarter || Math.Abs(_lastPowerLogW - chargeW) >= MaterialPowerChangeW))
                {
                    _lastPowerLogQuarter = nowQuarter;
                    _lastPowerLogW = chargeW;

                    if (chargeW > requestedW)
                        _logger.LogWarning(
                            $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: charge raised to solar surplus " +
                            $"({requestedW:F0}W → {chargeW:F0}W, NetLoadWh={qi?.NetLoadWh ?? 0.0:F0})");
                    else
                        _logger.LogWarning(
                            $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: charge clamped to what is still wanted " +
                            $"({requestedW:F0}W → {chargeW:F0}W, roomWh={roomWh:F0}, limitWh={limitWh:F0})");
                }

                // Expected flow stays the tapered plan power (it drives the SOC path); the
                // unthrottled field carries what was actually commanded, so the next taper fit
                // divides by a real setpoint.
                qi?.SetPlanPower(Math.Min(planned.PowerW, chargeW), 0, chargeW);
                return new PlanAction { Mode = Modes.Charging, PowerW = chargeW, RequestedPowerW = chargeW };
            }

            if (planned.Mode == Modes.Discharging)
            {
                // What to command. The plan's own power is what the measured capability expects to
                // come out and drives the SOC path; the batteries derate themselves, so asking for
                // less than they can do only guarantees we stay under it.
                double requestedW = RequestedDischargePowerW(planned);

                // Energy still bound by the plan, so the clamp below judges the command, not the
                // expectation — otherwise a request above the plan power would look unaffordable.
                double requiredWh = requestedW > 10 ? requestedW * 0.25 : dischargeStepWh;

                // Clamp to the energy available above the reserve. The planner plans down to
                // exactly minSoc, so an extra fixed margin dropped the last discharge quarter.
                double availableWh = socWh - minSocWh;

                bool dischargeGuardWasHeld = _dischargeGuardHeld;
                _dischargeGuardHeld = GuardHolds(dischargeGuardWasHeld, availableWh, stopWh, releaseWh);

                if (_dischargeGuardHeld)
                {
                    if (!dischargeGuardWasHeld)
                        _logger.LogWarning(
                            $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: GUARD_DISCHARGE_NO_ENERGY → HoldReserve " +
                            $"(socWh={socWh:F0}, minSocWh={minSocWh:F0}, availableWh={availableWh:F0}, releaseWh={releaseWh:F0})");

                    // Was ZeroNetHome: covered the house from just-bought energy or below the reserve.
                    return HoldAction(qi);
                }

                if (requiredWh > availableWh)
                {
                    double clampedW = availableWh * 4.0;
                    _logger.LogWarning(
                        $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: discharge clamped to available energy " +
                        $"({requestedW:F0}W → {clampedW:F0}W, availableWh={availableWh:F0})");

                    // Command and expectation coincide here: the clamp is the reserve talking,
                    // not the hardware, so this IS what should flow.
                    var clamped = new PlanAction
                    {
                        Mode = Modes.Discharging,
                        PowerW = clampedW,
                        RequestedPowerW = clampedW
                    };
                    qi?.SetPlanPower(0, clampedW, clampedW);
                    return clamped;
                }

                // Former runtime FIFO guard removed: acquisition cost is handled in the planner
                // (Candidate B real price, Candidate A via SessyOptions.StockCostEurPerKWh).

                // Expected flow stays the plan power (it drives the SOC path); the unthrottled
                // field carries what is actually commanded, so the next capability fit is measured
                // against a real setpoint.
                qi?.SetPlanPower(0, planned.PowerW, requestedW);
                return new PlanAction
                {
                    Mode = Modes.Discharging,
                    PowerW = requestedW,
                    RequestedPowerW = requestedW
                };
            }

            // HoldReserve is executed as planned: the P1 target already decides per moment between
            // storing surplus and idling, so no remapping here.
            if (planned.Mode == Modes.HoldReserve)
            {
                qi?.SetMode(Modes.HoldReserve);
                qi?.SetPlanPower(0, 0);
                return planned;
            }

            // The plan is the single truth: ZeroNetHome and Disabled run as planned.
            return planned;
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private async Task SavePlanAsync(string reason)
        {
            try
            {
                var savedAt = _timeZoneService.Now;
                var planId = Guid.NewGuid();
                var signature = CalculatePriceSignature(_quarterlyInfos);

                var actions = _planByTime.Select(kvp =>
                {
                    var qi = _quarterlyInfos.FirstOrDefault(q => q.Time == kvp.Key);
                    return new SessyData.Model.PlannedAction
                    {
                        PlanId = planId,
                        Time = kvp.Key,
                        Mode = kvp.Value.Mode.ToString(),
                        PowerW = kvp.Value.PowerW,
                        SavedAt = savedAt,
                        ObjectiveEur = _lastPlanObjectiveEur,
                        PriceSignature = signature,
                        Reason = reason,
                        ChargeLeftWh = qi?.ChargeLeftWh ?? 0.0
                    };
                }).ToList();

                await _plannedActionDataService.SavePlanAsync(actions).ConfigureAwait(false);
                _logger.LogInformation($"Plan saved ({actions.Count} quarters, reason: {reason}).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not save plan: {ex.Message}");
            }
        }
    }
}