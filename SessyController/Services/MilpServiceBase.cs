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
        protected readonly ChargeCostBasisService _chargeCostBasisService;
        protected readonly ThrottleAnalysisService _throttleAnalysisService;
        protected readonly WeatherService _weatherService;

        protected SettingsService _settingsService;
        protected Settings _settingsConfig;
        protected SessyBatteryConfig _sessyBatteryConfig;
        protected IDisposable? _sessyBatteryConfigSubscription;
        private string? _configChangedReason;

        // ── Plan state ───────────────────────────────────────────────────────

        protected List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();

        /// <summary>
        /// Throttle ratio applied per quarter (realized/target). Used to recover the
        /// throttle-free target power from the throttled solver output for reporting and for
        /// the throttle-ratio denominator. Defaults to 1.0 (no throttle) when absent.
        /// </summary>
        protected Dictionary<DateTime, double> _throttleRatioByTime = new();

        /// <summary>Charge throttle ratio per quarter (see _throttleRatioByTime).</summary>
        protected Dictionary<DateTime, double> _chargeThrottleRatioByTime = new();
        // Battery SOC (Wh) at the end of each quarter, taken directly from the solver so
        // the displayed SOC matches the plan exactly (single source of truth).
        private Dictionary<DateTime, double> _planSocWhByTime = new();
        private Dictionary<DateTime, double> _plannedSocByQuarter = new();
        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _minSocWhByTime = new();
        private Dictionary<DateTime, double> _maxSocWhByTime = new();

        /// <summary>
        /// Below this many Wh a (dis)charge is not worth issuing to the hardware: the setpoint
        /// would be a trickle, and the inverter's own idle draw would eat it. Used by the
        /// execution guards to decide between clamping the action and dropping it to ZeroNetHome.
        /// </summary>
        private const double MinimumUsefulWh = 25.0;

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

        internal sealed record PlanAction { public Modes Mode; public double PowerW; }

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
            ChargeCostBasisService chargeCostBasisService,
            ThrottleAnalysisService throttleAnalysisService,
            WeatherService weatherService)
        {
            _logger = logger;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
            _taxesDataService = taxesDataService;
            _plannedActionDataService = plannedActionDataService;
            _plannedQuarterDataService = plannedQuarterDataService;
            _chargeCostBasisService = chargeCostBasisService;
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
                        PlannedUnthrottledPowerW = qi.PlannedChargePowerW > 0
                            ? qi.PlannedUnthrottledPowerW
                            : -qi.PlannedUnthrottledPowerW,
                        PlannedChargeLeftWh = qi.ChargeLeftWh,
                        SellingPriceEurKWh = qi.SellingPrice,
                        BuyingPriceEurKWh = qi.BuyingPrice,
                        SolarForecastW = qi.SolarPowerPerQuarterInWatts,
                        ConsumptionForecastW = qi.EstimatedConsumptionPerQuarterInWatts
                    }).ToList();

                await _plannedQuarterDataService.AddOrUpdate(plannedQuarters,
                    (item, set) => set.FirstOrDefault(q => q.Time == item.Time)).ConfigureAwait(false);
            }
        }

        public async Task<(Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter)
        {
            var action = await GetExecutableActionAsync(nowQuarter).ConfigureAwait(false);
            return (action.Mode, action.PowerW);
        }

        public bool HasPlanFor(DateTime quarter) => _planByTime.ContainsKey(quarter);
        public bool IsNettingActive(DateTime quarter) => _nettingByTime.TryGetValue(quarter, out var n) ? n : true;

        public void ApplyRuntimeOverride(DateTime time, Modes mode, double powerW)
            => _planByTime[time] = new PlanAction { Mode = mode, PowerW = powerW };

        public async Task ClearPlanAsync()
        {
            _planByTime.Clear();
            _lastPriceSignature = null;
            _lastBuildTime = null;
            _lastPlanObjectiveEur = 0.0;
            _lastPlanQuarterCount = 0;
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
                RecentHistory = history,
            };
        }

        public double GetCurrentSocDeviationPct(DateTime now, double currentSocWh)
        {
            double capacityWh = _batteryContainer.GetTotalCapacity();
            if (capacityWh <= 0) return 0.0;
            var nowQuarter = now.DateFloorQuarter();
            if (_plannedSocByQuarter.TryGetValue(nowQuarter, out double expectedSocWh))
                return Math.Abs(currentSocWh - expectedSocWh) / capacityWh * 100.0;
            return 0.0;
        }

        public void Dispose() => _sessyBatteryConfigSubscription?.Dispose();

        // ── Context builder ──────────────────────────────────────────────────

        /// <summary>
        /// Builds per-quarter context:
        ///   _nettingByTime   — netting flag from Taxes
        ///   _minSocWhByTime  — minimum SOC (night reserve)
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

            // Night reserve cap (default 33%)
            double nightCapRatio = _settingsConfig.NightReserveCapPct > 0
                ? _settingsConfig.NightReserveCapPct / 100.0
                : 0.33;
            double reserveSafetyFactor = _settingsConfig.ReserveSafetyFactor > 0
                ? _settingsConfig.ReserveSafetyFactor
                : 1.10;

            for (int i = 0; i < ordered.Count; i++)
            {
                var qi = ordered[i];

                // ── minSoc: reserve for the next no-solar window ─────────────
                double nightReserveWh = 0.0;
                bool solarSeen = false;
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    var future = ordered[j];
                    if (future.NetLoadWh <= 0.0)
                    {
                        if (solarSeen)
                            break; // Second solar window = tomorrow — stop here.
                        solarSeen = true;
                        continue;
                    }
                    nightReserveWh += future.NetLoadWh;
                }
                double minSocWh = Math.Min(nightReserveWh * reserveSafetyFactor, capWh * nightCapRatio);

                // maxSoc = full capacity. The grid-balance solver decides for itself
                // whether to store solar surplus or export it, so no artificial headroom
                // is needed — that previously forced pointless early dumping.
                double maxSocWh = capWh;

                _minSocWhByTime[qi.Time] = minSocWh;
                _maxSocWhByTime[qi.Time] = maxSocWh;
            }

            // Bridge reserve: at the last known-price quarter, ensure enough SOC to
            // cover predicted-price window consumption.
            var lastKnown = ordered.LastOrDefault(q => !q.IsPriceExpected);
            if (lastKnown != null)
            {
                var predictedWindow = _quarterlyInfos
                    .Where(q => q.IsPriceExpected && q.Time > lastKnown.Time)
                    .OrderBy(q => q.Time)
                    .ToList();

                double bridgeWh = 0.0;
                foreach (var pq in predictedWindow)
                {
                    if (pq.NetLoadWh <= 0.0) break;
                    bridgeWh += pq.NetLoadWh;
                }
                bridgeWh *= reserveSafetyFactor;

                if (_minSocWhByTime.TryGetValue(lastKnown.Time, out var existing))
                    _minSocWhByTime[lastKnown.Time] = Math.Max(existing, bridgeWh);
                else
                    _minSocWhByTime[lastKnown.Time] = bridgeWh;
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
            else if (Math.Abs(GetCurrentSocDeviationPct(nowQuarter, currentSocWh)) > SocDeviationThresholdPct)
            {
                reason = $"SOC deviation exceeded {SocDeviationThresholdPct}%"; forced = true;
            }
            else if (_lastSpeculativeSolveQuarter != nowQuarter)
            {
                reason = "Quarterly speculative solve"; forced = false;
            }

            if (reason == null) return false;

            double previousObjective = _lastPlanObjectiveEur;
            int previousQuarterCount = _lastPlanQuarterCount;
            _logger.LogInformation($"Solving plan: {reason} (forced={forced})");

            bool built = await BuildMilpPlanAsync(currentSocWh).ConfigureAwait(false);

            if (!forced) _lastSpeculativeSolveQuarter = nowQuarter;

            if (!built)
            {
                _logger.LogWarning("MILP solve failed — keeping previous plan.");
                return false;
            }

            // Compare €/quarter, not the raw total: the remaining horizon shrinks every quarter
            // (fixed calendar window, shorter "future" as the day progresses), so a later solve's
            // total is naturally smaller than an earlier one even when it makes strictly better use
            // of the current situation. Comparing rates keeps the guard meaningful across horizon
            // lengths instead of freezing the plan at whichever solve happened to see the most hours.
            if (!forced && previousQuarterCount > 0 && _lastPlanQuarterCount > 0)
            {
                double previousRate = previousObjective / previousQuarterCount;
                double newRate = _lastPlanObjectiveEur / _lastPlanQuarterCount;

                if (previousRate > 0 && newRate <= previousRate)
                {
                    _logger.LogInformation(
                        $"Speculative solve rejected: {newRate:F4} <= {previousRate:F4} EUR/quarter " +
                        $"({_lastPlanObjectiveEur:F4}/{_lastPlanQuarterCount} <= {previousObjective:F4}/{previousQuarterCount}).");
                    _lastPlanObjectiveEur = previousObjective;
                    _lastPlanQuarterCount = previousQuarterCount;
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
        /// maxSoc is reduced by solar surplus so the solver is never forced to discharge
        /// just to stay feasible when the battery is full and solar is producing.
        /// </summary>
        protected List<SocBound> BuildSocBounds(List<QuarterlyInfo> quarters, double socKWh, double capKWh)
        {
            return quarters.Select(q =>
            {
                double mn = _minSocWhByTime.TryGetValue(q.Time, out var minV) ? minV / 1000.0 : 0.0;
                double mx = _maxSocWhByTime.TryGetValue(q.Time, out var maxV) ? maxV / 1000.0 : capKWh;

                mn = Math.Max(0.0, Math.Min(mn, capKWh));

                double solarSurplusKWh = q.NetLoadWh < 0.0 ? -q.NetLoadWh / 1000.0 : 0.0;
                mx = Math.Min(mx, capKWh - solarSurplusKWh);
                mx = Math.Max(mn, Math.Min(mx, capKWh));
                mx = Math.Max(mx, mn + 0.01);

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

            foreach (var p in result.Plan)
            {
                Modes mode;
                double powerW;

                switch (p.Mode)
                {
                    case ActionMode.Charge:
                        mode = Modes.Charging;
                        powerW = Math.Round(p.ChargeKW * 1000.0, 0);
                        break;
                    case ActionMode.Discharge:
                        mode = Modes.Discharging;
                        powerW = Math.Round(p.DischargeKW * 1000.0, 0);
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

                newPlan[p.Start] = new PlanAction { Mode = mode, PowerW = powerW };
                newSoc[p.Start] = p.SocEndKWh * 1000.0;
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
            _lastPlanObjectiveEur = result.ObjectiveEur;
            _lastPlanQuarterCount = quarterCount;
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
                {
                    double unthrottled = Unthrottle(qi.Time, act.PowerW, charging: true);
                    qi.SetPlanPower(act.PowerW, 0, unthrottled);
                }
                else if (act.Mode == Modes.Discharging)
                {
                    double unthrottled = Unthrottle(qi.Time, act.PowerW, charging: false);
                    qi.SetPlanPower(0, act.PowerW, unthrottled);
                }
                else
                    qi.SetPlanPower(0, 0);
            }
        }

        /// <summary>
        /// Recovers the throttle-free target power from the throttled solver output:
        /// target = throttled / ratio. Returns the throttled value unchanged when no ratio
        /// is known or the ratio is non-positive.
        /// </summary>
        private double Unthrottle(DateTime time, double throttledPowerW, bool charging)
        {
            var map = charging ? _chargeThrottleRatioByTime : _throttleRatioByTime;
            if (map.TryGetValue(time, out var ratio) && ratio > 0.0)
                return throttledPowerW / ratio;
            return throttledPowerW;
        }

        /// <summary>
        /// Projects the FIFO cost basis forward through the plan and stores the average
        /// cost basis per quarter on each QuarterlyInfo. Starts from the current measured
        /// cost-basis layers, then tracks the per-quarter SOC change from the simulation
        /// (ChargeLeftWh): a rising SOC adds a layer (solar free / grid at buy price), a
        /// falling SOC removes the oldest energy first. Using the SOC delta captures
        /// ZeroNetHome discharge too, so the cost-basis line stays consistent with the
        /// displayed SOC line.
        /// </summary>
        private async Task ProjectCostBasisAsync(double currentSocWh)
        {
            try
            {
                var snapshot = await _chargeCostBasisService.GetSnapshotAsync().ConfigureAwait(false);

                // Local FIFO copy seeded with the current real layers.
                var layers = new LinkedList<(double Wh, double Cost)>();
                foreach (var l in snapshot.Layers)
                    layers.AddLast((l.Wh, l.CostEurPerKWh));

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();
                var ordered = _quarterlyInfos
                    .Where(q => q.Time >= nowQuarter)
                    .OrderBy(q => q.Time)
                    .ToList();

                double prevSoc = currentSocWh;

                foreach (var qi in ordered)
                {
                    double soc = qi.ChargeLeftWh;
                    double delta = soc - prevSoc;
                    prevSoc = soc;

                    if (delta > 1.0)
                    {
                        // SOC rose: energy added. Solar covers it first (free), rest is grid.
                        double solarWh = qi.SolarPowerPerQuarterHour > 0
                            ? qi.SolarPowerPerQuarterHour * 1000.0 * 0.25
                            : 0.0;
                        double solarPart = Math.Min(delta, Math.Max(solarWh, 0.0));
                        double gridPart = Math.Max(delta - solarPart, 0.0);

                        if (solarPart > 1.0) layers.AddLast((solarPart, 0.0));
                        if (gridPart > 1.0) layers.AddLast((gridPart, qi.BuyingPrice));
                    }
                    else if (delta < -1.0)
                    {
                        // SOC fell: energy removed (discharge or ZeroNetHome). Pop oldest.
                        double remaining = -delta;
                        while (remaining > 1.0 && layers.First != null)
                        {
                            var f = layers.First.Value;
                            if (f.Wh <= remaining)
                            {
                                remaining -= f.Wh;
                                layers.RemoveFirst();
                            }
                            else
                            {
                                layers.First.Value = (f.Wh - remaining, f.Cost);
                                remaining = 0.0;
                            }
                        }
                    }

                    // Average cost basis of the projected battery contents this quarter.
                    double totalWh = 0.0, totalCost = 0.0;
                    foreach (var l in layers)
                    {
                        totalWh += l.Wh;
                        totalCost += l.Wh / 1000.0 * l.Cost;
                    }
                    qi.SetProjectedCostBasis(totalWh > 1.0 ? totalCost / (totalWh / 1000.0) : 0.0);
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

                // Prefer the SOC computed by the solver itself (single source of truth);
                // only simulate for quarters the solver did not cover (e.g. predicted-price
                // window beyond the horizon).
                if (_planSocWhByTime.TryGetValue(qi.Time, out var solverSoc))
                {
                    soc = Clamp(solverSoc, 0.0, capWh);
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
                    soc = Clamp(soc - netLoadWh, 0.0, capWh);
                    qi.SetChargeNeeded(minSocWh);
                }

                qi.SetChargeLeft(soc);
                qi.SetMode(act.Mode);

                if (act.Mode == Modes.Charging)
                    qi.SetPlanPower(act.PowerW > 0 ? act.PowerW : _batteryContainer.GetChargingCapacityInWattsPerHour(), 0);
                else if (act.Mode == Modes.Discharging)
                    qi.SetPlanPower(0, act.PowerW > 0 ? act.PowerW : _batteryContainer.GetDischargingCapacityInWattsPerHour());
                else
                    qi.SetPlanPower(0, 0);
            }
        }

        // ── Runtime action ───────────────────────────────────────────────────

        private async Task<PlanAction> GetExecutableActionAsync(DateTime nowQuarter)
        {
            if (!_planByTime.TryGetValue(nowQuarter, out var planned))
                return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };

            double capWh = _batteryContainer.GetTotalCapacity();
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * BatteryConstants.FullThresholdRatio;

            double minSocWh = _minSocWhByTime.TryGetValue(nowQuarter, out var mn) ? mn : 0.0;
            double maxSocWh = _maxSocWhByTime.TryGetValue(nowQuarter, out var mx) ? mx : capWh;
            maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

            var qi = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);

            if (planned.Mode == Modes.Charging)
            {
                // Don't actively charge when solar surplus is already filling the battery.
                // NOTE: intentionally NOT written into _planByTime — this is a live safety
                // check, not a plan revision. Overwriting the stored plan here would make a
                // single, possibly transient trip (a noisy SOC reading, a momentary reading lag)
                // stick for the rest of the quarter, since every later tick in the same quarter
                // would then check against the already-downgraded action instead of the original
                // plan. Returning the downgrade without persisting it lets each tick re-evaluate
                // the real, current state fresh.
                if (qi != null && qi.NetLoadWh < -chargeStepWh)
                {
                    _logger.LogWarning(
                        $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: GUARD_CHARGE_SOLAR_SURPLUS → ZeroNetHome " +
                        $"(NetLoadWh={qi.NetLoadWh:F0}, -chargeStepWh={-chargeStepWh:F0})");
                    var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    qi.SetMode(Modes.ZeroNetHome);
                    qi.SetPlanPower(0, 0);
                    return nzh;
                }

                // Clamp the charge to the room that is actually left instead of rejecting it.
                // Two earlier faults are fixed here: the test used chargeStepWh (FULL charging
                // capacity) regardless of what was planned, and it was all-or-nothing — with
                // room for 1647 Wh and a 1650 Wh step it charged 0 Wh, so the top of the battery
                // was never reached. The discharge branch already sized itself from planned.PowerW.
                double plannedChargeWh = planned.PowerW > 10 ? planned.PowerW * 0.25 : chargeStepWh;
                double roomWh = maxSocWh - socWh;

                if (roomWh <= MinimumUsefulWh)
                {
                    _logger.LogWarning(
                        $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: GUARD_CHARGE_NO_ROOM → ZeroNetHome " +
                        $"(socWh={socWh:F0}, maxSocWh={maxSocWh:F0}, roomWh={roomWh:F0})");
                    var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    qi?.SetMode(Modes.ZeroNetHome);
                    qi?.SetPlanPower(0, 0);
                    return nzh;
                }

                if (plannedChargeWh > roomWh)
                {
                    double clampedW = roomWh * 4.0;
                    _logger.LogInformation(
                        $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: charge clamped to remaining room " +
                        $"({planned.PowerW:F0}W → {clampedW:F0}W, roomWh={roomWh:F0})");

                    var clamped = new PlanAction { Mode = Modes.Charging, PowerW = clampedW };
                    qi?.SetPlanPower(clampedW, 0);
                    return clamped;
                }

                return planned;
            }

            if (planned.Mode == Modes.Discharging)
            {
                double requiredWh = planned.PowerW > 10 ? planned.PowerW * 0.25 : dischargeStepWh;

                // Clamp the discharge to the energy actually available above the reserve instead
                // of rejecting it. The planner deliberately plans down to exactly minSoc, so the
                // final discharge quarter always landed a few dozen Wh short of the old
                // "minSocWh + 50" test and was dropped entirely — the bottom of the usable range
                // was never delivered. Not persisted, so a transient SOC dip cannot forfeit the
                // rest of the quarter.
                double availableWh = socWh - minSocWh;

                if (availableWh <= MinimumUsefulWh)
                {
                    _logger.LogWarning(
                        $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: GUARD_DISCHARGE_NO_ENERGY → ZeroNetHome " +
                        $"(socWh={socWh:F0}, minSocWh={minSocWh:F0}, availableWh={availableWh:F0})");
                    var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    qi?.SetMode(Modes.ZeroNetHome);
                    qi?.SetPlanPower(0, 0);
                    return nzh;
                }

                if (requiredWh > availableWh)
                {
                    double clampedW = availableWh * 4.0;
                    _logger.LogInformation(
                        $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: discharge clamped to available energy " +
                        $"({planned.PowerW:F0}W → {clampedW:F0}W, availableWh={availableWh:F0})");

                    var clamped = new PlanAction { Mode = Modes.Discharging, PowerW = clampedW };
                    qi?.SetPlanPower(0, clampedW);
                    return clamped;
                }

                // NOTE: the previous runtime FIFO cost-basis guard was removed here. It
                // re-checked the current quarter against only the *oldest* layer price, a
                // cruder gate than the solver's own comparison across the whole horizon.
                // Acquisition cost is handled in the planner instead: energy charged inside
                // the horizon is priced at the real quarter price (Candidate B), and energy
                // already in the battery carries SessyOptions.StockCostEurPerKWh — the FIFO
                // weighted-average — as a reservation price (Candidate A).

                return planned;
            }

            // ZeroNetHome — decide between ZNH (store surplus) and Disabled (battery off).
            //
            // The forecast for the current quarter can be badly wrong (e.g. solar forecast
            // far too low on a sunny day), which would wrongly flag a deficit and Disable the
            // battery — exporting solar surplus to the grid for almost nothing. Base this
            // decision on the measured NetLoad of the last completed quarter instead, which
            // reflects the real situation right now.
            if (qi != null)
            {
                var prevQuarter = _quarterlyInfos
                    .Where(q => q.Time < nowQuarter)
                    .OrderByDescending(q => q.Time)
                    .FirstOrDefault();

                // NetLoad < 0 means solar surplus. Prefer ZeroNetHome so the surplus charges
                // the battery instead of being exported cheaply. Only fall through to Disabled
                // when there is a genuine deficit and the sell price is too low to make
                // discharging worthwhile.
                double effectiveNetLoadWh = prevQuarter?.NetLoadWh ?? qi.NetLoadWh;

                if (effectiveNetLoadWh < 0.0 && socWh < maxSocWh)
                {
                    if (planned.Mode != Modes.ZeroNetHome)
                        _logger.LogWarning(
                            $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: PLANNED_MODE_ALREADY_{planned.Mode} " +
                            $"→ ZERO_NET_HOME_SOLAR_SURPLUS (effectiveNetLoadWh={effectiveNetLoadWh:F0}, socWh={socWh:F0}, maxSocWh={maxSocWh:F0})");
                    var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    qi.SetMode(Modes.ZeroNetHome);
                    qi.SetPlanPower(0, 0);
                    return nzh;
                }

                if (effectiveNetLoadWh >= 0.0 &&
                    qi.SellingPrice < _settingsService.CycleCost)
                {
                    _logger.LogWarning(
                        $"GetExecutableAction[{nowQuarter:dd-MM HH:mm}]: PLANNED_MODE_{planned.Mode}_BELOW_CYCLE_COST " +
                        $"→ Disabled (sellingPrice={qi.SellingPrice:F4}, cycleCost={_settingsService.CycleCost:F4})");
                    var disabled = new PlanAction { Mode = Modes.Disabled, PowerW = 0 };
                    qi.SetMode(Modes.Disabled);
                    qi.SetPlanPower(0, 0);
                    return disabled;
                }
            }

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