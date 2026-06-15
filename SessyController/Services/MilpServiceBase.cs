using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
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

        protected SettingsService _settingsService;
        protected Settings _settingsConfig;
        protected SessyBatteryConfig _sessyBatteryConfig;
        protected IDisposable? _sessyBatteryConfigSubscription;
        private string? _configChangedReason;

        // ── Plan state ───────────────────────────────────────────────────────

        protected List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();
        private Dictionary<DateTime, double> _plannedSocByQuarter = new();
        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _minSocWhByTime = new();
        private Dictionary<DateTime, double> _maxSocWhByTime = new();

        private string? _lastRebuildReason;
        private DateTime? _lastBuildTime;
        private double _lastPlanObjectiveEur;
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
            ChargeCostBasisService chargeCostBasisService)
        {
            _logger = logger;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
            _taxesDataService = taxesDataService;
            _plannedActionDataService = plannedActionDataService;
            _plannedQuarterDataService = plannedQuarterDataService;
            _chargeCostBasisService = chargeCostBasisService;
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
            double solarHeadroomFactor = _settingsConfig.SolarHeadroomSafetyFactor > 0
                ? _settingsConfig.SolarHeadroomSafetyFactor
                : 1.05;

            for (int i = 0; i < ordered.Count; i++)
            {
                var qi = ordered[i];

                // ── minSoc: reserve for the next no-solar window ─────────────
                // Skip current solar surplus, then sum positive net load until
                // the next solar period starts (= tomorrow morning).
                // This ensures the battery holds enough for tonight even when
                // the current quarter is in the middle of the day.
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

                // ── maxSoc: leave headroom for upcoming solar charging ────────
                // Find the largest single-quarter solar surplus in the next few hours.
                double maxSolarChargeWh = 0.0;
                for (int j = i + 1; j < ordered.Count && j < i + 16; j++)
                {
                    double surplus = -ordered[j].NetLoadWh;
                    if (surplus > maxSolarChargeWh) maxSolarChargeWh = surplus;
                }
                double maxSocWh = capWh - maxSolarChargeWh * solarHeadroomFactor;
                maxSocWh = Math.Max(maxSocWh, minSocWh);
                maxSocWh = Math.Min(maxSocWh, capWh);

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
            _logger.LogInformation($"Solving plan: {reason} (forced={forced})");

            bool built = await BuildMilpPlanAsync(currentSocWh).ConfigureAwait(false);

            if (!forced) _lastSpeculativeSolveQuarter = nowQuarter;

            if (!built)
            {
                _logger.LogWarning("MILP solve failed — keeping previous plan.");
                return false;
            }

            if (!forced && previousObjective > 0 && _lastPlanObjectiveEur <= previousObjective)
            {
                _logger.LogInformation(
                    $"Speculative solve rejected: {_lastPlanObjectiveEur:F4} <= {previousObjective:F4} EUR.");
                _lastPlanObjectiveEur = previousObjective;
                return false;
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
            _lastPlanObjectiveEur = result.ObjectiveEur;
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
                    qi.SetPlanPower(act.PowerW, 0);
                else if (act.Mode == Modes.Discharging)
                    qi.SetPlanPower(0, act.PowerW);
                else
                    qi.SetPlanPower(0, 0);
            }
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

                if (act.Mode == Modes.Charging)
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
                if (qi != null && qi.NetLoadWh < -chargeStepWh)
                {
                    var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    _planByTime[nowQuarter] = nzh;
                    qi.SetMode(Modes.ZeroNetHome);
                    qi.SetPlanPower(0, 0);
                    return nzh;
                }

                if (socWh + chargeStepWh > maxSocWh + 0.001)
                {
                    var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    _planByTime[nowQuarter] = nzh;
                    qi?.SetMode(Modes.ZeroNetHome);
                    qi?.SetPlanPower(0, 0);
                    return nzh;
                }

                return planned;
            }

            if (planned.Mode == Modes.Discharging)
            {
                double requiredWh = planned.PowerW > 10 ? planned.PowerW * 0.25 : dischargeStepWh;

                if (socWh - requiredWh < minSocWh + 50.0)
                {
                    var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    _planByTime[nowQuarter] = nzh;
                    qi?.SetMode(Modes.ZeroNetHome);
                    qi?.SetPlanPower(0, 0);
                    return nzh;
                }

                // Cost-basis guard: only discharge to grid when the sell price beats the
                // acquisition cost of the oldest stored energy (FIFO) plus cycle cost.
                // Otherwise discharging realizes a loss on that energy.
                if (qi != null)
                {
                    double oldestCost = await _chargeCostBasisService
                        .GetOldestLayerPriceEur().ConfigureAwait(false);

                    if (qi.SellingPrice <= oldestCost + _settingsConfig.CycleCost)
                    {
                        var nzh = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                        _planByTime[nowQuarter] = nzh;
                        qi.SetMode(Modes.ZeroNetHome);
                        qi.SetPlanPower(0, 0);
                        return nzh;
                    }
                }

                return planned;
            }

            // ZeroNetHome — switch to Disabled if selling price is too low.
            if (qi != null && qi.NetLoadWh >= 0.0 &&
                qi.SellingPrice < _settingsConfig.CycleCost + _settingsConfig.NetZeroHomeMinProfit)
                return new PlanAction { Mode = Modes.Disabled, PowerW = 0 };

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