using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services
{
    /// <summary>
    /// Encapsulates all MILP planning logic for battery charge/discharge optimization.
    ///
    /// Responsibilities:
    /// - Building tariff context (netting, min/max SOC envelopes, self-use value)
    /// - Deciding when to rebuild the plan (price signature change, new quarter)
    /// - Running the parallel split MILP search via BatteryArbitrageMilp
    /// - Post-processing: self-consumption policy, SOC feasibility, solar smoothing
    /// - SOC simulation write-back for visualization
    ///
    /// BatteriesService owns hardware interaction (execute actions, read SOC, store
    /// performance). MilpService owns planning logic only.
    /// </summary>
    public sealed class MilpService
    {
        private readonly LoggingService<MilpService> _logger;
        private readonly IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private readonly IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;
        private readonly BatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;
        private readonly TaxesDataService _taxesDataService;

        private SettingsConfig _settingsConfig;
        private SessyBatteryConfig _sessyBatteryConfig;

        private IDisposable? _settingsConfigSubscription;
        private IDisposable? _sessyBatteryConfigSubscription;

        // ── Plan state ───────────────────────────────────────────────────────

        private List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();

        // Per-quarter context built by BuildTariffContextAsync.
        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _minSocWhByTime = new();
        private Dictionary<DateTime, double> _maxSocWhByTime = new();
        private Dictionary<DateTime, double> _futureSelfUseValueByTime = new();

        // Rebuild throttling — avoid re-solving when nothing has changed.
        private DateTime? _lastPlannedQuarter;
        private int? _lastPriceSignature;

        // ── Constants ────────────────────────────────────────────────────────

        private const int MilpTimeLimitMs = 5000;

        private const double ReserveWh = 0.0;
        private const double EmptyHysteresisWh = 50.0;
        private const double FullThresholdRatio = 0.995;
        private const double NumericEpsWh = 0.001;

        private const int SelfUseLookAheadQuarters = 96;       // 24 hours
        private const double ReserveSafetyFactor = 1.10;       // keep 10% extra energy
        private const double SolarHeadroomSafetyFactor = 1.05;
        private const double CheapRefillToleranceEur = 0.01;
        private const double CheapGridChargeThresholdEur = 0.05;
        private const double ExportPremiumEur = 0.02;

        // ── Internal plan action record ──────────────────────────────────────

        internal sealed record PlanAction
        {
            public Modes Mode;
            public double PowerW;
        }

        public MilpService(
            LoggingService<MilpService> logger,
            IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
            IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
            BatteryContainer batteryContainer,
            TimeZoneService timeZoneService,
            TaxesDataService taxesDataService)
        {
            _logger = logger;
            _settingsConfigMonitor = settingsConfigMonitor;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
            _taxesDataService = taxesDataService;

            _settingsConfig = settingsConfigMonitor.CurrentValue
                ?? throw new InvalidOperationException("ManagementSettings missing");
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue
                ?? throw new InvalidOperationException("Sessy:Batteries missing");

            _settingsConfigSubscription = _settingsConfigMonitor.OnChange(s => _settingsConfig = s);
            _sessyBatteryConfigSubscription = _sessyBatteryConfigMonitor.OnChange(s => _sessyBatteryConfig = s);
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Main entry point called by BatteriesService each cycle.
        /// Builds tariff context, rebuilds plan if needed, applies post-processing
        /// and writes the SOC simulation back into quarterlyInfos.
        /// </summary>
        public async Task BuildPlanAsync(
            List<QuarterlyInfo> quarterlyInfos,
            double currentSocWh)
        {
            _quarterlyInfos = quarterlyInfos;

            await BuildTariffContextAsync().ConfigureAwait(false);

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            await RebuildPlanIfNeeded(nowQuarter, currentSocWh).ConfigureAwait(false);

            ApplySelfConsumptionPolicy();

            await EnsureEnergyForPlannedDischargeAsync(currentSocWh).ConfigureAwait(false);

            WritePlanIntoQuarterlyInfos();

            await WriteBackSocSimulationAsync(currentSocWh).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the executable action for the current quarter,
        /// with runtime SOC guards applied.
        /// </summary>
        public async Task<(Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter)
        {
            var action = await GetExecutableActionInternalAsync(nowQuarter).ConfigureAwait(false);
            return (action.Mode, action.PowerW);
        }

        /// <summary>
        /// Applies a runtime override to the plan for a specific quarter.
        /// Used by BatteriesService when curtailment overrides the planned action.
        /// </summary>
        public void ApplyRuntimeOverride(DateTime time, Modes mode, double powerW)
        {
            _planByTime[time] = new PlanAction { Mode = mode, PowerW = powerW };
        }

        /// <summary>
        /// Returns the netting flag for the given quarter.
        /// </summary>
        public bool IsNettingActive(DateTime quarter)
            => _nettingByTime.TryGetValue(quarter, out var n) ? n : true;

        /// <summary>
        /// Returns true when the plan contains an entry for the given quarter.
        /// </summary>
        public bool HasPlanFor(DateTime quarter)
            => _planByTime.ContainsKey(quarter);

        /// <summary>
        /// Invalidates the current plan, forcing a full rebuild on the next cycle.
        /// </summary>
        public void InvalidatePlan()
        {
            _lastPlannedQuarter = null;
            _lastPriceSignature = null;
        }

        public void Dispose()
        {
            _settingsConfigSubscription?.Dispose();
            _sessyBatteryConfigSubscription?.Dispose();
        }

        // ── Tariff context ───────────────────────────────────────────────────

        /// <summary>
        /// Build tariff-aware min/max SOC envelopes for each quarter.
        ///
        /// min SOC:
        /// - when netting is disabled, keep enough energy for future expensive positive-load quarters
        ///
        /// max SOC:
        /// - always keep enough free headroom for the strongest upcoming net solar charging excursion
        /// </summary>
        private async Task BuildTariffContextAsync()
        {
            _nettingByTime.Clear();
            _minSocWhByTime.Clear();
            _maxSocWhByTime.Clear();
            _futureSelfUseValueByTime.Clear();

            if (_quarterlyInfos.Count == 0)
                return;

            var ordered = _quarterlyInfos
                .OrderBy(q => q.Time)
                .ToList();

            // Cache taxes per day to avoid repeated DB calls.
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

            double capWh = _batteryContainer.GetTotalCapacity();

            static double Clamp(double value, double min, double max)
                => value < min ? min : (value > max ? max : value);

            for (int i = 0; i < ordered.Count; i++)
            {
                var qi = ordered[i];
                bool netting = _nettingByTime[qi.Time];

                var futureWindow = ordered
                    .Skip(i + 1)
                    .Take(SelfUseLookAheadQuarters)
                    .Select(x => new
                    {
                        x.Time,
                        Buy = x.BuyingPrice,
                        NetLoadWh = x.EstimatedConsumptionPerQuarterInWatts - x.SolarPowerPerQuarterInWatts
                    })
                    .ToList();

                if (futureWindow.Count == 0)
                {
                    _minSocWhByTime[qi.Time] = ReserveWh;
                    _maxSocWhByTime[qi.Time] = capWh;
                    _futureSelfUseValueByTime[qi.Time] = qi.BuyingPrice;
                    continue;
                }

                // Weighted average future buy price over positive-load quarters.
                var futurePositiveLoad = futureWindow
                    .Where(x => x.NetLoadWh > 0.0)
                    .ToList();

                double selfUseValue;

                if (futurePositiveLoad.Count == 0)
                {
                    selfUseValue = qi.BuyingPrice;
                }
                else
                {
                    double totalLoadWh = futurePositiveLoad.Sum(x => x.NetLoadWh);

                    selfUseValue = totalLoadWh > 0.0
                        ? futurePositiveLoad.Sum(x => x.Buy * x.NetLoadWh) / totalLoadWh
                        : qi.BuyingPrice;
                }

                _futureSelfUseValueByTime[qi.Time] = selfUseValue;

                // Minimum SOC: reserve energy for future expensive consumption.
                // Strategy differs by netting status:
                //
                // Netting ON: minSoc = 0 (export is valuable, no need to hold reserves)
                //
                // Netting OFF: hold enough energy to cover future positive-load quarters
                //   where buy price > current price + tolerance. This avoids importing
                //   expensive energy when we could have kept the battery charged.
                //   Additionally, reserve energy for evening consumption after solar drops.
                double minSocWh = ReserveWh;

                if (!netting)
                {
                    // Energy needed for future expensive positive-load quarters.
                    double reserveForExpensiveImport = futureWindow
                        .Where(x => x.NetLoadWh > 0.0)
                        .Where(x => x.Buy > qi.BuyingPrice + CheapRefillToleranceEur)
                        .Sum(x => x.NetLoadWh);

                    minSocWh = reserveForExpensiveImport * ReserveSafetyFactor;
                    minSocWh = Clamp(minSocWh, ReserveWh, capWh);
                }

                _minSocWhByTime[qi.Time] = minSocWh;

                // Maximum SOC: reserve empty space for the strongest upcoming
                // contiguous net solar surplus excursion on the same calendar day.
                var solarHeadroomWindow = futureWindow
                    .Where(x => x.Time.Date == qi.Time.Date)
                    .ToList();

                double cumulativeFillWh = 0.0;
                double maxCumulativeFillWh = 0.0;

                foreach (var future in solarHeadroomWindow)
                {
                    double netSurplusWh = Math.Max(0.0, -future.NetLoadWh);

                    if (netSurplusWh > 0.0)
                    {
                        cumulativeFillWh += netSurplusWh;
                    }
                    else
                    {
                        cumulativeFillWh = 0.0;
                    }

                    if (cumulativeFillWh > maxCumulativeFillWh)
                        maxCumulativeFillWh = cumulativeFillWh;
                }

                double solarHeadroomWh = Clamp(maxCumulativeFillWh * SolarHeadroomSafetyFactor, 0.0, capWh);
                double maxSocWh = Clamp(capWh - solarHeadroomWh, ReserveWh, capWh);

                if (maxSocWh < minSocWh)
                    maxSocWh = minSocWh;

                _maxSocWhByTime[qi.Time] = maxSocWh;
            }
        }

        // ── Plan building ────────────────────────────────────────────────────

        private async Task<bool> RebuildPlanIfNeeded(DateTime nowQuarter, double currentSocWh)
        {
            int currentSignature = CalculatePriceSignature(_quarterlyInfos);

            bool needRebuild =
                _planByTime.Count == 0 ||
                _lastPlannedQuarter == null ||
                _lastPlannedQuarter.Value != nowQuarter ||
                _lastPriceSignature == null ||
                _lastPriceSignature.Value != currentSignature;

            if (!needRebuild)
                return false;

            bool built = await BuildMilpPlanAsync(currentSocWh).ConfigureAwait(false);

            if (!built)
            {
                _logger.LogWarning("MILP solve did not produce a usable plan. Keeping previous plan.");
                return false;
            }

            _lastPlannedQuarter = nowQuarter;
            _lastPriceSignature = currentSignature;

            return true;
        }

        private static int CalculatePriceSignature(List<QuarterlyInfo> infos)
        {
            unchecked
            {
                int hash = 17;

                foreach (var q in infos.OrderBy(x => x.Time))
                {
                    hash = hash * 23 + q.Time.GetHashCode();
                    hash = hash * 23 + Math.Round(q.BuyingPrice, 4).GetHashCode();
                    hash = hash * 23 + Math.Round(q.SellingPrice, 4).GetHashCode();
                    hash = hash * 23 + Math.Round(q.EstimatedConsumptionPerQuarterInWatts, 1).GetHashCode();
                    hash = hash * 23 + Math.Round(q.SolarPowerPerQuarterInWatts, 1).GetHashCode();
                }

                return hash;
            }
        }

        private async Task<bool> BuildMilpPlanAsync(double socWh)
        {
            try
            {
                double capacityWh = _batteryContainer.GetTotalCapacity();
                double capacityKWh = capacityWh / 1000.0;
                double socKWh = socWh / 1000.0;

                double maxChargeKW = _sessyBatteryConfig.TotalChargingCapacity / 1000.0;
                double maxDischargeKW = _sessyBatteryConfig.TotalDischargingCapacity / 1000.0;

                var nowQuarterTime = _timeZoneService.Now.DateFloorQuarter();

                // Only plan from the next quarter onwards — the current quarter is already executing.
                var allQuarters = _quarterlyInfos
                    .Where(q => q.Time >= nowQuarterTime.AddMinutes(15))
                    .OrderBy(q => q.Time)
                    .ToList();

                if (allQuarters.Count == 0)
                    return false;

                // ── Parallel split search ─────────────────────────────────────────
                // Each task solves two plan segments (split at a different quarter) and
                // returns the combined objective. The split with the highest combined
                // objective is selected as the final plan. A full-horizon solve is
                // always included as a candidate.
                int totalQuarters = allQuarters.Count;
                int minSplitIndex = Math.Min(4, totalQuarters - 1);

                var tasks = Enumerable
                    .Range(minSplitIndex, totalQuarters - minSplitIndex)
                    .Select(splitIndex => Task.Run(() =>
                    {
                        var splitTime = allQuarters[splitIndex].Time;
                        var seg1 = allQuarters.Take(splitIndex).ToList();
                        var seg2 = allQuarters.Skip(splitIndex).ToList();

                        if (seg1.Count == 0)
                            return (SplitTime: splitTime, Combined: double.MinValue,
                                    Plan1: (PlanResult?)null, Plan2: (PlanResult?)null);

                        var opt = new SessyOptions(
                            QuarterMinutes: 15,
                            ActiveQuarterPenaltyEur: 0.0,
                            ForbidSimultaneousChargeDischarge: true,
                            TimeLimitMs: MilpTimeLimitMs,
                            CycleCostEurPerKWh: _settingsConfig.CycleCost);

                        var r1 = SolvePlanSegment(seg1, socKWh, capacityKWh, maxChargeKW, maxDischargeKW, opt);

                        if (r1 == null)
                            return (SplitTime: splitTime, Combined: double.MinValue,
                                    Plan1: (PlanResult?)null, Plan2: (PlanResult?)null);

                        double socAfterSeg1 = r1.Plan.Count > 0
                            ? r1.Plan[r1.Plan.Count - 1].SocEndKWh
                            : socKWh;

                        double combined = r1.ObjectiveEur;
                        PlanResult? r2 = null;

                        if (seg2.Count > 0)
                        {
                            r2 = SolvePlanSegment(seg2, socAfterSeg1, capacityKWh, maxChargeKW, maxDischargeKW, opt);
                            if (r2 != null)
                                combined += r2.ObjectiveEur;
                        }

                        return (SplitTime: splitTime, Combined: combined,
                                Plan1: (PlanResult?)r1, Plan2: (PlanResult?)r2);
                    }))
                    .ToList();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                sw.Stop();

                _logger.LogWarning($"MILP parallel search: {tasks.Count} splits evaluated in {sw.ElapsedMilliseconds}ms | now={nowQuarterTime:HH:mm}");

                var best = results
                    .OrderByDescending(r => r.Combined)
                    .FirstOrDefault();

                if (best.Plan1 == null)
                    return false;

                _logger.LogWarning($"MILP split search: best split={best.SplitTime:HH:mm}, " +
                    $"combined={best.Combined:F4} EUR | timeLimit={MilpTimeLimitMs}ms | now={nowQuarterTime:HH:mm}");

                // ── Merge both plan segments into _planByTime ────────────────────
                var newPlan = new Dictionary<DateTime, PlanAction>();

                foreach (var result in new[] { best.Plan1, best.Plan2 })
                {
                    if (result?.Plan == null)
                        continue;

                    foreach (var p in result.Plan)
                    {
                        double powerW;
                        Modes mode;

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

                            default:
                                mode = Modes.ZeroNetHome;
                                powerW = 0.0;
                                break;
                        }

                        newPlan[p.Start] = new PlanAction { Mode = mode, PowerW = powerW };
                    }
                }

                foreach (var qi in _quarterlyInfos)
                {
                    if (!newPlan.ContainsKey(qi.Time))
                    {
                        // Preserve existing plan for quarters not covered by the MILP
                        // (e.g. the current quarter). Overwriting with ZeroNetHome would
                        // interrupt an active Charging action.
                        if (_planByTime.TryGetValue(qi.Time, out var existing))
                            newPlan[qi.Time] = existing;
                        else
                            newPlan[qi.Time] = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    }
                }

                _planByTime = newPlan;

                // Override the last quarter of each day to ZeroNetHome unless the price
                // is negative. The MILP sometimes plans unnecessary Charging in the last
                // quarter because there are no future quarters to use the energy.
                var lastQuartersByDay = _planByTime.Keys
                    .GroupBy(t => t.Date)
                    .Select(g => g.Max())
                    .ToHashSet();

                foreach (var lastQuarter in lastQuartersByDay)
                {
                    if (_planByTime.TryGetValue(lastQuarter, out var lastAct) &&
                        lastAct.Mode == Modes.Charging)
                    {
                        var lastQi = _quarterlyInfos.FirstOrDefault(q => q.Time == lastQuarter);

                        if (lastQi == null || !lastQi.PriceIsNegative)
                        {
                            _planByTime[lastQuarter] = new PlanAction
                            {
                                Mode = Modes.ZeroNetHome,
                                PowerW = 0
                            };
                        }
                    }
                }

                _logger.LogWarning($"MILP plan built: plan1={best.Plan1.Optimal}, obj1={best.Plan1.ObjectiveEur:F4} EUR" +
                    (best.Plan2 != null ? $" | plan2={best.Plan2.Optimal}, obj2={best.Plan2.ObjectiveEur:F4} EUR" : "") +
                    $" | split={best.SplitTime:HH:mm} | timeLimit={MilpTimeLimitMs}ms | now={nowQuarterTime:HH:mm} | quarters={allQuarters.Count}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"BuildMilpPlan failed: {ex.ToDetailedString()}");
                return false;
            }
        }

        /// <summary>
        /// Solves a single MILP segment for the given quarters and initial SOC.
        /// Returns null if the solve failed or produced no usable plan.
        /// </summary>
        private PlanResult? SolvePlanSegment(
            List<QuarterlyInfo> quarters,
            double initialSocKWh,
            double capacityKWh,
            double maxChargeKW,
            double maxDischargeKW,
            SessyOptions opt)
        {
            if (quarters.Count == 0)
                return null;

            double capacityWh = capacityKWh * 1000.0;

            var pricePoints = quarters
                .Select(q =>
                {
                    double netLoadWh = q.EstimatedConsumptionPerQuarterInWatts - q.SolarPowerPerQuarterInWatts;
                    bool netting = _nettingByTime.TryGetValue(q.Time, out var n) ? n : true;

                    // When netting is disabled, use the future weighted average buy price
                    // as the self-use value. This rewards discharging for own consumption
                    // (avoided import cost) even when the export rate is very low.
                    // When netting is active, self-use value equals the sell price —
                    // the MILP already optimises correctly in that case.
                    double selfUseValue = netting
                        ? q.SellingPrice
                        : (_futureSelfUseValueByTime.TryGetValue(q.Time, out var suv) ? suv : q.BuyingPrice);

                    return new PricePoint(q.Time, q.BuyingPrice, q.SellingPrice, netLoadWh, selfUseValue);
                })
                .ToList();

            var socBounds = new List<SocBound>();

            foreach (var q in quarters)
            {
                double minKWh = (_minSocWhByTime.TryGetValue(q.Time, out var mn) ? mn : 0.0) / 1000.0;
                double maxKWh = (_maxSocWhByTime.TryGetValue(q.Time, out var mx) ? mx : capacityWh) / 1000.0;

                minKWh = Math.Max(0.0, Math.Min(minKWh, capacityKWh));
                maxKWh = Math.Max(minKWh, Math.Min(maxKWh, capacityKWh));

                // maxSocKWh must never fall below the initial SOC.
                maxKWh = Math.Max(maxKWh, initialSocKWh);

                socBounds.Add(new SocBound(q.Time, minKWh, maxKWh));
            }

            var spec = new BatterySpec(
                CapacityKWh: capacityKWh,
                InitialSocKWh: initialSocKWh,
                MinSocKWh: 0.0,
                MaxSocKWh: capacityKWh,
                MaxChargeKW: maxChargeKW,
                MaxDischargeKW: maxDischargeKW,
                ChargeEfficiency: 0.95,
                DischargeEfficiency: 0.95);

            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt, socBounds);

            if (result == null || result.Plan == null || result.Plan.Count == 0)
                return null;

            return result;
        }

        // ── Post-processing ──────────────────────────────────────────────────

        /// <summary>
        /// When netting is disabled:
        /// - do not force active grid charging except at truly cheap prices
        /// - solar surplus should be absorbed through ZeroNetHome, not active Charging
        /// - export-style discharge only when clearly superior to keeping energy
        ///
        /// For all quarters (netting on or off):
        /// - ZeroNetHome is only used when the buying price justifies it via NetZeroHomeMinProfit.
        /// </summary>
        private void ApplySelfConsumptionPolicy()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0)
                return;

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time))
            {
                bool netting = _nettingByTime.TryGetValue(qi.Time, out var n) ? n : true;
                var act = _planByTime[qi.Time];
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                double selfUseValue = _futureSelfUseValueByTime.TryGetValue(qi.Time, out var suv)
                    ? suv
                    : qi.BuyingPrice;

                if (!netting)
                {
                    // Solar surplus should not trigger active grid charging.
                    if (netLoadWh < -1.0 && act.Mode == Modes.Charging)
                    {
                        act.Mode = Modes.ZeroNetHome;
                        act.PowerW = 0;
                    }
                    else if (act.Mode == Modes.Charging && qi.BuyingPrice > CheapGridChargeThresholdEur)
                    {
                        act.Mode = Modes.ZeroNetHome;
                        act.PowerW = 0;
                    }
                    else if (act.Mode == Modes.Discharging)
                    {
                        // When netting is disabled: allow discharging for own consumption
                        // (positive net load = household needs more than solar produces).
                        // Only block pure export-style discharge where net load is zero or
                        // negative (solar already covers consumption) and sell price is too
                        // low to justify the cycle cost.
                        bool dischargingForOwnConsumption = netLoadWh > 0.0;

                        if (!dischargingForOwnConsumption)
                        {
                            double exportThreshold = selfUseValue + _settingsConfig.CycleCost + ExportPremiumEur;

                            if (qi.SellingPrice < exportThreshold)
                            {
                                act.Mode = Modes.ZeroNetHome;
                                act.PowerW = 0;
                            }
                        }
                    }
                }

                bool hasSolarSurplus = netLoadWh < 0.0;

                // Downgrade ZeroNetHome to Disabled when self-consumption is not economically justified.
                if (act.Mode == Modes.ZeroNetHome &&
                    !hasSolarSurplus &&
                    qi.SellingPrice < _settingsConfig.CycleCost + _settingsConfig.NetZeroHomeMinProfit)
                {
                    act.Mode = Modes.Disabled;
                    act.PowerW = 0;
                }
            }
        }

        /// <summary>
        /// Simulate the plan forward and remove actions that violate min/max SOC envelopes.
        /// </summary>
        private async Task EnsureEnergyForPlannedDischargeAsync(double startSocWh)
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0)
                return;

            var now = _timeZoneService.Now.DateFloorQuarter();
            double capWh = _batteryContainer.GetTotalCapacity();
            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            static double Clamp(double value, double min, double max)
                => value < min ? min : (value > max ? max : value);

            var future = _quarterlyInfos
                .OrderBy(q => q.Time)
                .Where(q => q.Time >= now)
                .ToList();

            if (future.Count == 0)
                return;

            foreach (var qi in future)
            {
                if (!_planByTime.ContainsKey(qi.Time))
                    _planByTime[qi.Time] = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
            }

            const int maxIterations = 400;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                bool changed = false;
                double soc = Clamp(startSocWh, 0.0, capWh);

                foreach (var qi in future)
                {
                    var act = _planByTime[qi.Time];
                    double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                    double minSocWh = _minSocWhByTime.TryGetValue(qi.Time, out var minSoc)
                        ? minSoc
                        : ReserveWh;

                    double maxSocWh = _maxSocWhByTime.TryGetValue(qi.Time, out var maxSoc)
                        ? maxSoc
                        : capWh;

                    maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

                    if (act.Mode == Modes.Charging)
                    {
                        if (soc + chargeStepWh > maxSocWh + NumericEpsWh)
                        {
                            act.Mode = Modes.ZeroNetHome;
                            act.PowerW = 0;
                            changed = true;
                            soc = Clamp(soc - netLoadWh, 0.0, capWh);
                        }
                        else
                        {
                            soc = Clamp(soc + chargeStepWh, 0.0, capWh);
                        }
                    }
                    else if (act.Mode == Modes.Discharging)
                    {
                        if (soc - dischargeStepWh < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                        {
                            act.Mode = Modes.ZeroNetHome;
                            act.PowerW = 0;
                            changed = true;
                            soc = Clamp(soc - netLoadWh, 0.0, capWh);
                        }
                        else
                        {
                            soc = Clamp(soc - dischargeStepWh, 0.0, capWh);
                        }
                    }
                    else
                    {
                        soc = Clamp(soc - netLoadWh, 0.0, capWh);
                    }
                }

                if (!changed)
                    return;
            }

            _logger.LogWarning("EnsureEnergyForPlannedDischargeAsync: maxIterations reached; plan may still contain infeasible segments.");
        }

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
        /// Simulate SOC from NOW forward for visualization and diagnostics.
        /// </summary>
        private async Task WriteBackSocSimulationAsync(double soc)
        {
            if (_quarterlyInfos.Count == 0)
                return;

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();
            double capWh = _batteryContainer.GetTotalCapacity();
            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            static double Clamp(double value, double min, double max)
                => value < min ? min : (value > max ? max : value);

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time).Where(q => q.Time >= nowQuarter))
            {
                if (!_planByTime.TryGetValue(qi.Time, out var act))
                {
                    act = new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    _planByTime[qi.Time] = act;
                }

                double minSocWh = _minSocWhByTime.TryGetValue(qi.Time, out var minSoc)
                    ? minSoc
                    : ReserveWh;

                double maxSocWh = _maxSocWhByTime.TryGetValue(qi.Time, out var maxSoc)
                    ? maxSoc
                    : capWh;

                maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                double targetSocWh;

                if (act.Mode == Modes.Charging)
                {
                    if (soc + chargeStepWh > maxSocWh + NumericEpsWh)
                    {
                        act.Mode = Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = minSocWh;
                        soc = Clamp(soc - netLoadWh, 0.0, capWh);
                    }
                    else
                    {
                        soc = Clamp(soc + chargeStepWh, 0.0, capWh);
                        targetSocWh = maxSocWh;
                    }
                }
                else if (act.Mode == Modes.Discharging)
                {
                    if (soc - dischargeStepWh < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                    {
                        act.Mode = Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = minSocWh;
                        soc = Clamp(soc - netLoadWh, 0.0, capWh);
                    }
                    else
                    {
                        soc = Clamp(soc - dischargeStepWh, 0.0, capWh);
                        targetSocWh = minSocWh;
                    }
                }
                else
                {
                    targetSocWh = minSocWh;
                    soc = Clamp(soc - netLoadWh, 0.0, capWh);
                }

                qi.SetChargeNeeded(targetSocWh);
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

        private async Task<PlanAction> GetExecutableActionInternalAsync(DateTime nowQuarter)
        {
            if (!_planByTime.TryGetValue(nowQuarter, out var planned))
                return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };

            double capWh = _batteryContainer.GetTotalCapacity();
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            bool netting = _nettingByTime.TryGetValue(nowQuarter, out var n) ? n : true;

            double minSocWh = _minSocWhByTime.TryGetValue(nowQuarter, out var minSoc)
                ? minSoc : ReserveWh;

            double maxSocWh = _maxSocWhByTime.TryGetValue(nowQuarter, out var maxSoc)
                ? maxSoc : capWh;

            maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

            double selfUseValue = _futureSelfUseValueByTime.TryGetValue(nowQuarter, out var suv)
                ? suv : 0.0;

            var qi = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);

            // Block active grid charging when solar surplus is large enough to fill
            // the battery by itself via ZeroNetHome.
            if (qi != null && planned.Mode == Modes.Charging)
            {
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                if (netLoadWh < -chargeStepWh)
                    return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
            }

            if (planned.Mode == Modes.Charging)
            {
                if (socWh + chargeStepWh > maxSocWh + NumericEpsWh)
                    return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };

                if (!netting && qi != null && qi.BuyingPrice > CheapGridChargeThresholdEur)
                    return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };

                return planned;
            }

            if (planned.Mode == Modes.Discharging)
            {
                double requiredWh = planned.PowerW > 10
                    ? planned.PowerW * 0.25
                    : dischargeStepWh;

                if (socWh - requiredWh < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                    return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };

                if (!netting && qi != null)
                {
                    // Allow discharging for own consumption (positive net load).
                    // Only block pure export-style discharge when sell price is too low.
                    double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                    bool dischargingForOwnConsumption = netLoadWh > 0.0;

                    if (!dischargingForOwnConsumption)
                    {
                        double exportThreshold = selfUseValue + _settingsConfig.CycleCost + ExportPremiumEur;

                        if (qi.SellingPrice < exportThreshold)
                            return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                    }
                }

                return planned;
            }

            // Runtime guard for ZeroNetHome / Disabled.
            if (planned.Mode == Modes.ZeroNetHome || planned.Mode == Modes.Disabled)
            {
                if (qi != null)
                {
                    double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                    bool hasSolarSurplus = netLoadWh < 0.0;

                    if (!hasSolarSurplus &&
                        qi.SellingPrice < _settingsConfig.CycleCost + _settingsConfig.NetZeroHomeMinProfit)
                        return new PlanAction { Mode = Modes.Disabled, PowerW = 0 };
                }
            }

            return planned;
        }
    }
}