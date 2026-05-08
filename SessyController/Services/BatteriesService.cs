using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;
using static SessyData.Model.SessyWebControl;

namespace SessyController.Services
{
    /// <summary>
    /// Battery controller that combines:
    /// - day-ahead price arbitrage
    /// - tariff-aware behavior (netting on/off)
    /// - minimum SOC protection for future expensive consumption
    /// - maximum SOC protection to preserve headroom for future solar surplus
    /// - solar inverter curtailment during negative price periods
    /// </summary>
    public sealed class BatteriesService : BackgroundHeartbeatService
    {
        private readonly LoggingService<BatteriesService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        private readonly IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private readonly IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;

        private IDisposable? _settingsConfigSubscription;
        private IDisposable? _sessyBatteryConfigSubscription;

        private SettingsConfig _settingsConfig;
        private SessyBatteryConfig _sessyBatteryConfig;

        private readonly SemaphoreSlim _semaphore = new(1, 1);

        private IServiceScope _scope;

        private DayAheadMarketService _dayAheadMarketService;
        private SolarService _solarService;
        private BatteryContainer _batteryContainer;
        private TimeZoneService _timeZoneService;
        private ConsumptionMonitorService _consumptionMonitorService;
        private SessyWebControlDataService _sessyWebControlDataService;
        private PerformanceDataService? _performanceDataService;
        private TaxesDataService _taxesDataService;

        // Curtailment: throttles the solar inverter when price is negative and battery is full.
        private InverterCurtailmentService _inverterCurtailmentService;

        // IMPROVEMENT 5: Was incorrectly 'static' — multiple instances would share
        // the same list. Made private instance field.
        private List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();

        // Per-quarter context
        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _minSocWhByTime = new();
        private Dictionary<DateTime, double> _maxSocWhByTime = new();
        private Dictionary<DateTime, double> _futureSelfUseValueByTime = new();

        // Rebuild throttling
        private DateTime? _lastPlannedQuarter;
        private int? _lastPriceSignature;

        // Fixed MILP time limit per solve segment.
        private const int MilpTimeLimitMs = 5000;

        public bool IsManualOverride => _settingsConfig.ManualOverride;
        public bool WeAreInControl { get; private set; } = true;

        public delegate Task DataChangedDelegate();
        public event DataChangedDelegate? DataChanged;

        private sealed record PlanAction
        {
            public Modes Mode;
            public double PowerW;
        }

        // General thresholds
        private const double ReserveWh = 0.0;
        private const double EmptyHysteresisWh = 50.0;
        private const double FullThresholdRatio = 0.995;
        private const double NumericEpsWh = 0.001;

        // Planning heuristics
        private const int SelfUseLookAheadQuarters = 96;      // 24 hours
        private const double ReserveSafetyFactor = 1.10;      // keep 10% extra energy
        private const double SolarHeadroomSafetyFactor = 1.05;
        private const double CheapRefillToleranceEur = 0.01;
        private const double CheapGridChargeThresholdEur = 0.05;

        private const double ExportPremiumEur = 0.02;

        public BatteriesService(
            LoggingService<BatteriesService> logger,
            IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
            IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;

            _settingsConfigMonitor = settingsConfigMonitor;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;

            _settingsConfig = settingsConfigMonitor.CurrentValue ?? throw new InvalidOperationException("ManagementSettings missing");
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue ?? throw new InvalidOperationException("Sessy:Batteries missing");

            _settingsConfigSubscription = _settingsConfigMonitor.OnChange(settings => _settingsConfig = settings);
            _sessyBatteryConfigSubscription = _sessyBatteryConfigMonitor.OnChange(settings => _sessyBatteryConfig = settings);

            _scope = _serviceScopeFactory.CreateScope();

            _dayAheadMarketService = _scope.ServiceProvider.GetRequiredService<DayAheadMarketService>();
            _solarService = _scope.ServiceProvider.GetRequiredService<SolarService>();
            _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _consumptionMonitorService = _scope.ServiceProvider.GetRequiredService<ConsumptionMonitorService>();
            _sessyWebControlDataService = _scope.ServiceProvider.GetRequiredService<SessyWebControlDataService>();
            _performanceDataService = _scope.ServiceProvider.GetService<PerformanceDataService>();
            _taxesDataService = _scope.ServiceProvider.GetRequiredService<TaxesDataService>();
            _inverterCurtailmentService = _scope.ServiceProvider.GetRequiredService<InverterCurtailmentService>();

            _logger.LogInformation("BatteriesService starting");
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _scope.Dispose();
            return base.StopAsync(cancellationToken);
        }

        private async Task CleanUpWrongData()
        {
            await _performanceDataService.RemoveWrongData().ConfigureAwait(false);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("BatteriesService started ...");

            await CleanUpWrongData();

            var delaySeconds = 60;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await HeartBeatAsync().ConfigureAwait(false);

                    await Process(cancellationToken).ConfigureAwait(false);

                    delaySeconds = DataChanged == null ? 1 : 60;

                    if (DataChanged != null)
                        await DataChanged.Invoke().ConfigureAwait(false);

                    await StorePerformance().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"An error occurred while managing batteries. {ex.ToDetailedString()}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation during delay.
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay. {ex.ToDetailedString()}");
                }
            }

            _logger.LogWarning("BatteriesService stopped.");
        }

        public async Task Process(CancellationToken cancellationToken)
        {
            if (_dayAheadMarketService == null || !_dayAheadMarketService.IsInitialized())
                return;

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!await RefreshQuarterlyPrices().ConfigureAwait(false))
                    return;

                await _consumptionMonitorService.EstimateConsumptionInWattsPerQuarter(_quarterlyInfos).ConfigureAwait(false);
                await _solarService.GetExpectedSolarPower(_quarterlyInfos).ConfigureAwait(false);

                // IMPROVEMENT 4: SOC is fetched once per cycle and passed to all methods.
                // Previously GetStateOfChargeInWatts() was called 4 times per cycle
                // (each call is a network request to the Sessy).
                double currentSocWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

                // Build per-quarter tariff and SOC bounds from prices + forecasts.
                await BuildTariffContextAsync().ConfigureAwait(false);

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                await RebuildPlanIfNeeded(nowQuarter, currentSocWh).ConfigureAwait(false);

                // Apply self-consumption/export restrictions only when netting is disabled.
                ApplySelfConsumptionPolicy();

                // Final physical feasibility pass with min/max SOC envelopes.
                await EnsureEnergyForPlannedDischargeAsync(currentSocWh).ConfigureAwait(false);

                // Zero out solar forecast for quarters where the inverter will be shut
                // down: only during Charging quarters with negative prices. During those
                // quarters we get paid to consume from the grid, so our own solar
                // production directly reduces that benefit.
                // SmoothedSolarPower is recalculated after zeroing so the smoothing
                // window does not pull down adjacent quarters.
                if (_settingsConfig.SolarSystemShutsDownDuringNegativePrices)
                {
                    foreach (var qi in _quarterlyInfos.Where(q => q.PriceIsNegative &&
                        _planByTime.TryGetValue(q.Time, out var act) &&
                        act.Mode == Modes.Charging))
                    {
                        qi.SolarPowerPerQuarterHour = 0.0;
                    }

                    // Recalculate smoothed solar power after zeroing.
                    // Only average over quarters with the same solar state (zero or non-zero)
                    // to prevent the smoothing window from pulling down adjacent quarters.
                    var ordered = _quarterlyInfos.OrderBy(q => q.Time).ToList();
                    int windowSize = 8;

                    for (int i = 0; i < ordered.Count; i++)
                    {
                        bool isZero = ordered[i].SolarPowerPerQuarterHour == 0.0;
                        int start = Math.Max(0, i - windowSize / 2);
                        int end = Math.Min(ordered.Count - 1, i + windowSize / 2);

                        var range = ordered
                            .Skip(start)
                            .Take(end - start + 1)
                            .Where(h => (h.SolarPowerPerQuarterHour == 0.0) == isZero)
                            .ToList();

                        ordered[i].SmoothedSolarPower = range.Any()
                            ? range.Average(h => h.SolarPowerPerQuarterHour)
                            : 0.0;
                    }
                }

                WritePlanIntoQuarterlyInfos();
                await WriteBackSocSimulationAsync(currentSocWh).ConfigureAwait(false);

                if (await WeControlTheBatteries().ConfigureAwait(false))
                {
                    var executable = await GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

                    // ── Curtailment check ────────────────────────────────────────────────
                    // Signal the InverterCurtailmentService whether curtailment should be
                    // active. The actual inverter throttling runs in its own 5-second loop
                    // so it can react to real-time load changes (heat pump, AC, etc.)
                    // without waiting for the 60-second BatteriesService cycle.
                    //
                    // Curtailment condition: price is negative AND battery is full.
                    //
                    // When curtailment is active the Sessy must be Disabled (StopAll),
                    // NOT ZeroNetHome. NOM reacts to the grid current and would try to
                    // discharge the battery to compensate for the reduced inverter output —
                    // directly fighting the curtailment logic.
                    var nowQi = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);
                    bool priceIsNegative = nowQi?.PriceIsNegative ?? false;
                    bool batteryIsFull = await BatteryIsFullAsync().ConfigureAwait(false);

                    _inverterCurtailmentService.SetCurtailmentRequested(priceIsNegative, batteryIsFull);

                    // When the inverter is shut down (price negative, battery not full),
                    // zero out the solar forecast in QuarterlyInfos so the chart reflects
                    // the actual situation — no solar production during shutdown periods.
                    if (priceIsNegative && !batteryIsFull)
                    {
                        foreach (var qi in _quarterlyInfos.Where(q => q.PriceIsNegative))
                        {
                            qi.SolarPowerPerQuarterHour = 0.0;
                            qi.SmoothedSolarPower = 0.0;
                        }
                    }

                    // Curtailment is active in both shutdown (battery not full) and
                    // throttle (battery full) modes. In both cases the Sessy must be
                    // Disabled so NOM does not fight the inverter curtailment.
                    // Only force Disabled when the price is actually still negative —
                    // if the price turned positive but IsCurtailmentActive is still true
                    // due to a Modbus error, we should not block Charging.
                    if (_inverterCurtailmentService.IsCurtailmentActive && priceIsNegative)
                    {
                        executable = new PlanAction
                        {
                            Mode = Modes.Disabled,
                            PowerW = 0
                        };
                    }
                    // ── End curtailment check ────────────────────────────────────────────

                    if (!_planByTime.TryGetValue(nowQuarter, out var plannedNow) ||
                        plannedNow.Mode != executable.Mode ||
                        Math.Abs(plannedNow.PowerW - executable.PowerW) > 0.1)
                    {
                        ApplyRuntimeOverrideToPlan(nowQuarter, executable);
                        WritePlanIntoQuarterlyInfos();
                        await WriteBackSocSimulationAsync(currentSocWh).ConfigureAwait(false);
                    }

                    await ExecuteAction(executable).ConfigureAwait(false);
                }
                else
                {
#if !DEBUG
                    // Release curtailment when we lose control so the inverter
                    // is not left throttled if e.g. the supplier takes over.
                    _inverterCurtailmentService.SetCurtailmentRequested(false, false);
                    await _batteryContainer.StopAll().ConfigureAwait(false);
#endif
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"Unhandled exception in Process: {ex.ToDetailedString()}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<bool> RefreshQuarterlyPrices()
        {
            var prices = await _dayAheadMarketService.GetPrices().ConfigureAwait(false);

            _quarterlyInfos = prices
                .OrderBy(p => p.Time)
                .ToList();

            QuarterlyInfo.AddSmoothedPrices(_quarterlyInfos, 6);

            return _quarterlyInfos.Count > 0;
        }

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

                // Minimum SOC: only meaningful when netting is disabled.
                double minSocWh = ReserveWh;

                if (!netting)
                {
                    minSocWh = futureWindow
                        .Where(x => x.NetLoadWh > 0.0)
                        .Where(x => x.Buy > qi.BuyingPrice + CheapRefillToleranceEur)
                        .Sum(x => x.NetLoadWh);

                    minSocWh *= ReserveSafetyFactor;
                    minSocWh = Clamp(minSocWh, ReserveWh, capWh);
                }

                _minSocWhByTime[qi.Time] = minSocWh;

                // Maximum SOC: reserve empty space for the strongest upcoming
                // contiguous net solar surplus excursion.
                // Only look at the current calendar day to avoid reserving headroom
                // for tomorrow's solar today.
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
                        // Consumption > solar: fill streak resets completely.
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

                // IMPROVEMENT 4: socWh is passed in from Process() —
                // no additional network call to the Sessy needed.
                double socKWh = socWh / 1000.0;

                double maxChargeKW = _sessyBatteryConfig.TotalChargingCapacity / 1000.0;
                double maxDischargeKW = _sessyBatteryConfig.TotalDischargingCapacity / 1000.0;

                // Filter out historic quarters — _quarterlyInfos contains prices for
                // yesterday, today and tomorrow. Only quarters from the current quarter
                // onwards are relevant. The current quarter is included so that
                // GetExecutableActionForNowAsync always has a valid plan.
                var nowQuarterTime = _timeZoneService.Now.DateFloorQuarter();

                // Filter out historic quarters AND the current quarter.
                // The current quarter is already executing — including it allows the MILP
                // to plan actions (e.g. discharge at high prices) that are already partially
                // or fully in the past, leading to incorrect execution in the next cycle.
                var allQuarters = _quarterlyInfos
                    .Where(q => q.Time >= nowQuarterTime.AddMinutes(15))
                    .OrderBy(q => q.Time)
                    .ToList();

                if (allQuarters.Count == 0)
                    return false;

                // ── Parallel split search ─────────────────────────────────────────
                // Each task solves two plan segments (split at a different quarter) and
                // returns the combined objective. All tasks run concurrently so the
                // total wall time ≈ a single solve. The split with the highest
                // combined objective is selected as the final plan.
                // The search runs over ALL available quarters — no artificial horizon
                // limit is imposed so the solver finds the globally optimal split.
                // A full-horizon (no split) solve is always included as a candidate.
                // Split index starts at 4 (= 1 hour) so seg1 always has enough quarters
                // to produce a meaningful plan for the current and near-future quarters.
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
                            return (SplitTime: splitTime, Combined: double.MinValue, Plan1: (PlanResult?)null, Plan2: (PlanResult?)null);

                        var opt = new SessyOptions(
                            QuarterMinutes: 15,
                            ActiveQuarterPenaltyEur: 0.0,
                            ForbidSimultaneousChargeDischarge: true,
                            TimeLimitMs: MilpTimeLimitMs,
                            CycleCostEurPerKWh: _settingsConfig.CycleCost
                        );

                        var r1 = SolvePlanSegment(seg1, socKWh, capacityKWh, maxChargeKW, maxDischargeKW, opt);
                        if (r1 == null)
                            return (SplitTime: splitTime, Combined: double.MinValue, Plan1: (PlanResult?)null, Plan2: (PlanResult?)null);

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

                        return (SplitTime: splitTime, Combined: combined, Plan1: (PlanResult?)r1, Plan2: (PlanResult?)r2);
                    }))
                    .ToList();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                sw.Stop();

                _logger.LogWarning($"MILP parallel search: {tasks.Count} splits evaluated in {sw.ElapsedMilliseconds}ms | now={nowQuarterTime:HH:mm}");

                // Pick the split with the highest combined objective.
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

                        newPlan[p.Start] = new PlanAction
                        {
                            Mode = mode,
                            PowerW = powerW
                        };
                    }
                }

                foreach (var qi in _quarterlyInfos)
                {
                    if (!newPlan.ContainsKey(qi.Time))
                    {
                        // Preserve the existing plan for quarters not covered by the MILP
                        // (e.g. the current quarter when the MILP only plans from the next
                        // quarter onwards). Overwriting with ZeroNetHome would interrupt
                        // an active Charging action.
                        if (_planByTime.TryGetValue(qi.Time, out var existing))
                            newPlan[qi.Time] = existing;
                        else
                            newPlan[qi.Time] = new PlanAction
                            {
                                Mode = Modes.ZeroNetHome,
                                PowerW = 0
                            };
                    }
                }

                _planByTime = newPlan;

                // Override the last quarter of each day to ZeroNetHome unless the price
                // is negative. The MILP sometimes plans unnecessary Charging in the last
                // quarter because there are no future quarters to use the energy, making
                // the cycle cost appear neutral. This is always suboptimal.
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
                    return new PricePoint(q.Time, q.BuyingPrice, q.SellingPrice, netLoadWh);
                })
                .ToList();

            var socBounds = new List<SocBound>();

            foreach (var q in quarters)
            {
                double minKWh = (_minSocWhByTime.TryGetValue(q.Time, out var mn) ? mn : 0.0) / 1000.0;
                double maxKWh = (_maxSocWhByTime.TryGetValue(q.Time, out var mx) ? mx : capacityWh) / 1000.0;

                minKWh = Math.Max(0.0, Math.Min(minKWh, capacityKWh));
                maxKWh = Math.Max(minKWh, Math.Min(maxKWh, capacityKWh));

                // BUGFIX: maxSocKWh must never fall below the initial SOC.
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
                DischargeEfficiency: 0.95
            );

            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt, socBounds);

            if (result == null || result.Plan == null || result.Plan.Count == 0)
                return null;

            return result;
        }

        /// <summary>
        /// When netting is disabled:
        /// - do not force active grid charging except at truly cheap prices
        /// - solar surplus should be absorbed through ZeroNetHome, not active Charging
        /// - export-style discharge only when clearly superior to keeping energy
        ///
        /// For all quarters (netting on or off):
        /// - ZeroNetHome is only used when the buying price justifies it via NetZeroHomeMinProfit.
        ///   If the price is too low, Disabled is used instead to avoid unnecessary battery cycling.
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

                // No-netting specific checks: may downgrade Charging/Discharging to ZeroNetHome.
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
                        double exportThreshold = selfUseValue + _settingsConfig.CycleCost + ExportPremiumEur;

                        if (qi.SellingPrice < exportThreshold)
                        {
                            act.Mode = Modes.ZeroNetHome;
                            act.PowerW = 0;
                        }
                    }
                }

                bool hasSolarSurplus = netLoadWh < 0.0;

                // For all quarters: downgrade ZeroNetHome to Disabled when using the
                // battery for self-consumption is not economically justified.
                //
                // NOM is only worthwhile when the selling price covers the cycle cost
                // plus the minimum profit margin. Solar surplus is always free so NOM
                // is always justified when solar is producing more than consumption.
                //
                // Example: CycleCost=0.09, margin=0.02 → NOM only when SellingPrice >= 0.11
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
                {
                    _planByTime[qi.Time] = new PlanAction
                    {
                        Mode = Modes.ZeroNetHome,
                        PowerW = 0
                    };
                }
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

        /// <summary>
        /// Runtime-safe action for NOW.
        /// Enforces the same min/max SOC envelopes as the planner.
        /// </summary>
        private async Task<PlanAction> GetExecutableActionForNowAsync(DateTime nowQuarter)
        {
            if (!_planByTime.TryGetValue(nowQuarter, out var planned))
            {
                return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
            }

            double capWh = _batteryContainer.GetTotalCapacity();
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            bool netting = _nettingByTime.TryGetValue(nowQuarter, out var n) ? n : true;

            double minSocWh = _minSocWhByTime.TryGetValue(nowQuarter, out var minSoc)
                ? minSoc
                : ReserveWh;

            double maxSocWh = _maxSocWhByTime.TryGetValue(nowQuarter, out var maxSoc)
                ? maxSoc
                : capWh;

            maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

            double selfUseValue = _futureSelfUseValueByTime.TryGetValue(nowQuarter, out var suv)
                ? suv
                : 0.0;

            var qi = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);

            // Only block active grid charging when solar surplus is large enough to
            // fill the battery by itself via ZeroNetHome. If surplus is smaller than
            // the charge capacity, active charging is still worthwhile on top of the
            // free solar energy — especially when prices are low or negative.
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

                // Only restrict active charging by price when netting is disabled.
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

                // Only restrict export-style discharge when netting is disabled.
                if (!netting && qi != null)
                {
                    double exportThreshold = selfUseValue + _settingsConfig.CycleCost + ExportPremiumEur;

                    if (qi.SellingPrice < exportThreshold)
                        return new PlanAction { Mode = Modes.ZeroNetHome, PowerW = 0 };
                }

                return planned;
            }

            // Runtime guard for ZeroNetHome / Disabled:
            // NOM is only worthwhile when the selling price covers the cycle cost
            // plus the minimum profit margin. Solar surplus is always free.
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

        private async Task<bool> BatteryIsFullAsync()
        {
            double capWh = _batteryContainer.GetTotalCapacity();
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            return socWh >= capWh * FullThresholdRatio;
        }

        private void ApplyRuntimeOverrideToPlan(DateTime time, PlanAction action)
        {
            _planByTime[time] = action;
        }

        private async Task ExecuteAction(PlanAction action)
        {
#if !DEBUG
            if (_dayAheadMarketService.IsInitialized())
            {
                if (_settingsConfig.ManualOverride)
                {
                    await ExecuteManualOverride().ConfigureAwait(false);
                    return;
                }

                switch (action.Mode)
                {
                    case Modes.Charging:
                        if (action.PowerW > 10)
                            await _batteryContainer.StartCharging((int)Math.Round(action.PowerW)).ConfigureAwait(false);
                        else
                            await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;

                    case Modes.Discharging:
                        if (action.PowerW > 10)
                            await _batteryContainer.StartDisharging((int)Math.Round(action.PowerW)).ConfigureAwait(false);
                        else
                            await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;

                    case Modes.ZeroNetHome:
                        await _batteryContainer.StartNetZeroHome().ConfigureAwait(false);
                        break;

                    case Modes.Disabled:
                    default:
                        await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;
                }
            }
            else
            {
                await ExecuteManualOverride().ConfigureAwait(false);
            }
#else
            await Task.Delay(1).ConfigureAwait(false);
#endif
        }

        private async Task ExecuteManualOverride()
        {
#if !DEBUG
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHours != null && _settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartCharging(_batteryContainer.GetChargingCapacityInWattsPerHour()).ConfigureAwait(false);
            else if (_settingsConfig.ManualDischargingHours != null && _settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartDisharging(_batteryContainer.GetDischargingCapacityInWattsPerHour()).ConfigureAwait(false);
            else if (_settingsConfig.ManualNetZeroHomeHours != null && _settingsConfig.ManualNetZeroHomeHours.Contains(localTime.Hour))
                await _batteryContainer.StartNetZeroHome().ConfigureAwait(false);
            else
                await _batteryContainer.StopAll().ConfigureAwait(false);
#else
            await Task.Delay(1).ConfigureAwait(false);
#endif
        }

        private async Task<bool> WeControlTheBatteries()
        {
            var supplierInControl = await SupplierIsControllingTheBatteries().ConfigureAwait(false);
            var chargedInControl = _settingsConfig.ChargedInControl;

            WeAreInControl = !(supplierInControl || chargedInControl);

            SessyWebControlStatus status = SessyWebControlStatus.SessyWeb;

            if (!WeAreInControl)
            {
                if (_settingsConfig.ChargedInControl)
                    status = SessyWebControlStatus.Charged;

                if (supplierInControl)
                    status = SessyWebControlStatus.Provider;
            }

            var last = await _sessyWebControlDataService.Get(async set =>
            {
                var result = set.OrderByDescending(sc => sc.Time).FirstOrDefault();
                return await Task.FromResult(result).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (last == null || last.Status != status)
                await StoreStatus(status).ConfigureAwait(false);

            return WeAreInControl;
        }

        private async Task StoreStatus(SessyWebControlStatus status)
        {
            var controlList = new List<SessyWebControl>
            {
                new SessyWebControl
                {
                    Time = _timeZoneService.Now,
                    Status = status
                }
            };

            await _sessyWebControlDataService.Add(controlList).ConfigureAwait(false);
        }

        private async Task<bool> SupplierIsControllingTheBatteries()
        {
            foreach (var battery in _batteryContainer.Batteries)
            {
                var currentPowerStrategy = await battery.GetPowerStatus().ConfigureAwait(false);
                if (currentPowerStrategy.Sessy.StrategyOverridden)
                    return true;
            }

            return false;
        }

        private async Task StorePerformance()
        {
            if (_performanceDataService == null)
                return;

            var currentQuarterlyInfo = GetNextQuarterlyInfoInPlan();
            if (currentQuarterlyInfo == null)
                return;

            bool exists = await _performanceDataService.Exists(async set =>
            {
                var result = set.Any(pd => pd.Time == currentQuarterlyInfo.Time);
                return await Task.FromResult(result).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (exists)
                return;

            var totalCapacity = _batteryContainer.GetTotalCapacity();

            var performanceData = new List<Performance>
            {
                new Performance
                {
                    Time = currentQuarterlyInfo.Time,
                    MarketPrice = currentQuarterlyInfo.MarketPrice,
                    BuyingPrice = currentQuarterlyInfo.BuyingPrice,
                    SmoothedBuyingPrice = currentQuarterlyInfo.SmoothedBuyingPrice,
                    SellingPrice = currentQuarterlyInfo.SellingPrice,
                    SmoothedSellingPrice = currentQuarterlyInfo.SmoothedSellingPrice,
                    Profit = currentQuarterlyInfo.Profit,
                    EstimatedConsumptionPerQuarterHour = currentQuarterlyInfo.EstimatedConsumptionPerQuarterInWatts,
                    ChargeLeft = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false),
                    ChargeNeeded = currentQuarterlyInfo.ChargeNeededWh,
                    Charging = currentQuarterlyInfo.Charging,
                    Discharging = currentQuarterlyInfo.Discharging,
                    ZeroNetHome = currentQuarterlyInfo.ZeroNetHome,
                    Disabled = currentQuarterlyInfo.Disabled,
                    SolarPowerPerQuarterHour = currentQuarterlyInfo.SolarPowerPerQuarterHour,
                    SmoothedSolarPower = currentQuarterlyInfo.SmoothedSolarPower,
                    SolarGlobalRadiation = currentQuarterlyInfo.SolarGlobalRadiation,
                    ChargeLeftPercentage = currentQuarterlyInfo.ChargeLeftPercentage(totalCapacity),
                    DisplayState = currentQuarterlyInfo.GetDisplayMode(),
                    VisualizeInChart = currentQuarterlyInfo.VisualizeInChart(),
                }
            };

            await _performanceDataService.Add(performanceData).ConfigureAwait(false);
        }

        public List<QuarterlyInfo> GetQuarterlyInfos()
        {
            _semaphore.Wait();
            try
            {
                return _quarterlyInfos.ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> GetBatteryMode()
        {
            var now = _timeZoneService.Now.DateFloorQuarter();
            if (_planByTime.TryGetValue(now, out var act))
                return ChargingModes.GetDisplayMode(act.Mode);

            return "???";
        }

        public QuarterlyInfo? GetNextQuarterlyInfoInPlan()
        {
            var now = _timeZoneService.Now.DateFloorQuarter();

            return _quarterlyInfos
                .OrderBy(q => q.Time)
                .FirstOrDefault(q => q.Time >= now && _planByTime.ContainsKey(q.Time));
        }

        public async Task<double> getBatteryPercentage()
        {
            return await _batteryContainer.GetBatterPercentage().ConfigureAwait(false);
        }

        private bool _isDisposed;

        public override void Dispose()
        {
            if (_isDisposed) return;

            _settingsConfigSubscription?.Dispose();
            _sessyBatteryConfigSubscription?.Dispose();

            _quarterlyInfos.Clear();
            _planByTime.Clear();
            _nettingByTime.Clear();
            _minSocWhByTime.Clear();
            _maxSocWhByTime.Clear();
            _futureSelfUseValueByTime.Clear();

            _scope.Dispose();

            base.Dispose();
            _isDisposed = true;
        }
    }
}