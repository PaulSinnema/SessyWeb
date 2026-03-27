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

        private static List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();

        // Per-quarter context
        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _minSocWhByTime = new();
        private Dictionary<DateTime, double> _maxSocWhByTime = new();
        private Dictionary<DateTime, double> _futureSelfUseValueByTime = new();

        // Rebuild throttling
        private DateTime? _lastPlannedQuarter;
        private int? _lastPriceSignature;

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
        private const double EmptyHysteresisWh = 300.0;
        private const double FullThresholdRatio = 0.995;
        private const double NumericEpsWh = 0.001;

        // Planning heuristics
        private const int SelfUseLookAheadQuarters = 96;      // 24 hours
        private const double ReserveSafetyFactor = 1.10;      // keep 10% extra energy
        private const double SolarHeadroomSafetyFactor = 1.05;
        private const double CheapRefillToleranceEur = 0.01;
        private const double CheapGridChargeThresholdEur = 0.05;
        private const double ExportPremiumEur = 0.05;

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

            _logger.LogInformation("BatteriesService starting");
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _scope.Dispose();
            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("BatteriesService started ...");

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

                // Build per-quarter tariff and SOC bounds from prices + forecasts
                await BuildTariffContextAsync().ConfigureAwait(false);

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                // Rebuild the base arbitrage plan only when needed
                await RebuildPlanIfNeeded(nowQuarter).ConfigureAwait(false);

                // Apply economic gating first
                ApplyCycleCostToPlan();

                // Apply self-consumption/export restrictions only when netting is disabled
                ApplySelfConsumptionPolicy();

                // Final physical feasibility pass with min/max SOC envelopes
                await EnsureEnergyForPlannedDischargeAsync().ConfigureAwait(false);

                WritePlanIntoQuarterlyInfos();
                await WriteBackSocSimulationAsync().ConfigureAwait(false);

                if (await WeControlTheBatteries().ConfigureAwait(false))
                {
                    var executable = await GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

                    if (!_planByTime.TryGetValue(nowQuarter, out var plannedNow) ||
                        plannedNow.Mode != executable.Mode ||
                        Math.Abs(plannedNow.PowerW - executable.PowerW) > 0.1)
                    {
                        ApplyRuntimeOverrideToPlan(nowQuarter, executable);
                        WritePlanIntoQuarterlyInfos();
                        await WriteBackSocSimulationAsync().ConfigureAwait(false);
                    }

                    await ExecuteAction(executable).ConfigureAwait(false);
                }
                else
                {
#if !DEBUG
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

                // Weighted average future buy price over positive-load quarters
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

                // Minimum SOC:
                // only meaningful when netting is disabled
                double minSocWh = ReserveWh;

                if (!netting)
                {
                    // Find the cheapest future buying price in the look-ahead window
                    double cheapestFutureBuy = futureWindow.Min(x => x.Buy);

                    // Find the first quarter that is effectively a cheap refill moment
                    int refillIndex = futureWindow.FindIndex(x =>
                        x.Buy <= cheapestFutureBuy + CheapRefillToleranceEur);

                    var untilRefill = refillIndex >= 0
                        ? futureWindow.Take(refillIndex)
                        : futureWindow;

                    // Keep enough energy for all positive net load until the next cheap refill moment
                    minSocWh = untilRefill
                        .Where(x => x.NetLoadWh > 0.0)
                        .Sum(x => x.NetLoadWh);

                    minSocWh *= ReserveSafetyFactor;
                    minSocWh = Clamp(minSocWh, ReserveWh, capWh);
                }

                _minSocWhByTime[qi.Time] = minSocWh;

                _minSocWhByTime[qi.Time] = minSocWh;

                // Maximum SOC:
                // reserve empty space for the strongest upcoming contiguous net charging excursion from solar
                double cumulativeFillWh = 0.0;
                double maxCumulativeFillWh = 0.0;

                foreach (var future in futureWindow)
                {
                    // Natural battery delta under ZeroNetHome:
                    // negative net load means the battery tends to fill
                    double naturalFillWh = Math.Max(0.0, -future.NetLoadWh);

                    // Positive net load breaks the fill streak
                    if (future.NetLoadWh > 0.0)
                    {
                        cumulativeFillWh = Math.Max(0.0, cumulativeFillWh - future.NetLoadWh);
                    }
                    else
                    {
                        cumulativeFillWh += naturalFillWh;
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

        private async Task<bool> RebuildPlanIfNeeded(DateTime nowQuarter)
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

            bool built = BuildMilpPlan();

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

        private bool BuildMilpPlan()
        {
            try
            {
                double capacityWh = _batteryContainer.GetTotalCapacity();
                double capacityKWh = capacityWh / 1000.0;

                double socWh = _batteryContainer.GetStateOfChargeInWatts().GetAwaiter().GetResult();
                double socKWh = socWh / 1000.0;

                double maxChargeKW = _sessyBatteryConfig.TotalChargingCapacity / 1000.0;
                double maxDischargeKW = _sessyBatteryConfig.TotalDischargingCapacity / 1000.0;

                var pricePoints = _quarterlyInfos
                    .OrderBy(q => q.Time)
                    .Select(q =>
                    {
                        double netLoadWh = q.EstimatedConsumptionPerQuarterInWatts - q.SolarPowerPerQuarterInWatts;

                        return new PricePoint(
                            q.Time,
                            q.BuyingPrice,
                            q.SellingPrice,
                            netLoadWh
                        );
                    })
                    .ToList();

                var spec = new BatterySpec(
                    CapacityKWh: capacityKWh,
                    InitialSocKWh: socKWh,
                    MinSocKWh: 0.0,
                    MaxSocKWh: capacityKWh,
                    MaxChargeKW: maxChargeKW,
                    MaxDischargeKW: maxDischargeKW,
                    ChargeEfficiency: 0.95,
                    DischargeEfficiency: 0.95
                );

                var opt = new SessyOptions(
                    QuarterMinutes: 15,
                    ActiveQuarterPenaltyEur: 0.01,
                    ForbidSimultaneousChargeDischarge: true,
                    TimeLimitMs: 500
                );

                var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt);

                if (result == null || result.Plan == null || result.Plan.Count == 0)
                    return false;

                var newPlan = new Dictionary<DateTime, PlanAction>(result.Plan.Count);

                foreach (var p in result.Plan)
                {
                    double powerW;
                    ChargingModes.Modes mode;

                    switch (p.Mode)
                    {
                        case ActionMode.Charge:
                            mode = ChargingModes.Modes.Charging;
                            powerW = Math.Round(p.ChargeKW * 1000.0, 0);
                            break;

                        case ActionMode.Discharge:
                            mode = ChargingModes.Modes.Discharging;
                            powerW = Math.Round(p.DischargeKW * 1000.0, 0);
                            break;

                        default:
                            mode = ChargingModes.Modes.ZeroNetHome;
                            powerW = 0.0;
                            break;
                    }

                    newPlan[p.Start] = new PlanAction
                    {
                        Mode = mode,
                        PowerW = powerW
                    };
                }

                foreach (var qi in _quarterlyInfos)
                {
                    if (!newPlan.ContainsKey(qi.Time))
                    {
                        newPlan[qi.Time] = new PlanAction
                        {
                            Mode = ChargingModes.Modes.ZeroNetHome,
                            PowerW = 0
                        };
                    }
                }

                _planByTime = newPlan;
                _logger.LogInformation($"MILP plan built: optimal={result.Optimal}, objective={result.ObjectiveEur:F4} EUR");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"BuildMilpPlan failed: {ex.ToDetailedString()}");
                return false;
            }
        }

        /// <summary>
        /// Remove obviously unprofitable discharge quarters.
        /// </summary>
        private void ApplyCycleCostToPlan()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0)
                return;

            double minSpread = _settingsConfig.CycleCost;
            if (minSpread <= 0.0)
                return;

            double? minBuyOfPlannedChargeSoFar = null;

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time))
            {
                var act = _planByTime[qi.Time];

                if (act.Mode == ChargingModes.Modes.Charging && act.PowerW > 10)
                {
                    minBuyOfPlannedChargeSoFar = minBuyOfPlannedChargeSoFar == null
                        ? qi.BuyingPrice
                        : Math.Min(minBuyOfPlannedChargeSoFar.Value, qi.BuyingPrice);

                    continue;
                }

                if (act.Mode == ChargingModes.Modes.Discharging && act.PowerW > 10)
                {
                    double basis = minBuyOfPlannedChargeSoFar ?? qi.BuyingPrice;
                    double spread = qi.SellingPrice - basis;

                    if (spread < minSpread)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                    }
                }
            }
        }

        /// <summary>
        /// When netting is disabled:
        /// - do not force active grid charging except at truly cheap prices
        /// - solar surplus should be absorbed through ZeroNetHome, not active Charging
        /// - export-style discharge only when clearly superior to keeping energy
        /// </summary>
        private void ApplySelfConsumptionPolicy()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0)
                return;

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time))
            {
                bool netting = _nettingByTime.TryGetValue(qi.Time, out var n) ? n : true;
                if (netting)
                    continue;

                var act = _planByTime[qi.Time];
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                double selfUseValue = _futureSelfUseValueByTime.TryGetValue(qi.Time, out var suv)
                    ? suv
                    : qi.BuyingPrice;

                // Solar surplus should not trigger active grid charging
                if (netLoadWh < -1.0)
                {
                    act.Mode = ChargingModes.Modes.ZeroNetHome;
                    act.PowerW = 0;
                    continue;
                }

                if (act.Mode == ChargingModes.Modes.Charging)
                {
                    if (qi.BuyingPrice > CheapGridChargeThresholdEur)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                    }

                    continue;
                }

                if (act.Mode == ChargingModes.Modes.Discharging)
                {
                    double exportThreshold = selfUseValue + _settingsConfig.CycleCost + ExportPremiumEur;

                    if (qi.SellingPrice < exportThreshold)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Simulate the plan forward and remove actions that violate:
        /// - minimum SOC
        /// - maximum SOC (solar headroom)
        /// </summary>
        private async Task EnsureEnergyForPlannedDischargeAsync()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0)
                return;

            var now = _timeZoneService.Now.DateFloorQuarter();

            double capWh = _batteryContainer.GetTotalCapacity();
            double startSocWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

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
                        Mode = ChargingModes.Modes.ZeroNetHome,
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

                    if (act.Mode == ChargingModes.Modes.Charging)
                    {
                        if (soc + chargeStepWh > maxSocWh + NumericEpsWh)
                        {
                            act.Mode = ChargingModes.Modes.ZeroNetHome;
                            act.PowerW = 0;
                            changed = true;
                        }
                        else
                        {
                            soc = Math.Min(capWh, soc + chargeStepWh);
                        }
                    }
                    else if (act.Mode == ChargingModes.Modes.Discharging)
                    {
                        if (soc - dischargeStepWh < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                        {
                            act.Mode = ChargingModes.Modes.ZeroNetHome;
                            act.PowerW = 0;
                            changed = true;
                        }
                        else
                        {
                            soc = Math.Max(0.0, soc - dischargeStepWh);
                        }
                    }

                    soc = Clamp(soc - netLoadWh, 0.0, capWh);

                    // Natural surplus can still fill the battery, but never above capacity
                    if (soc > capWh)
                        soc = capWh;
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
                    qi.SetMode(ChargingModes.Modes.ZeroNetHome);
                    qi.SetPlanPower(0, 0);
                    continue;
                }

                qi.SetMode(act.Mode);

                if (act.Mode == ChargingModes.Modes.Charging)
                    qi.SetPlanPower(act.PowerW, 0);
                else if (act.Mode == ChargingModes.Modes.Discharging)
                    qi.SetPlanPower(0, act.PowerW);
                else
                    qi.SetPlanPower(0, 0);
            }
        }

        /// <summary>
        /// Simulate SOC from NOW forward.
        ///
        /// ChargeLeft = realized SOC after action + household effect
        /// ChargeNeeded = current quarter target:
        /// - Charging => upper target (max SOC)
        /// - Discharging => lower target (min SOC)
        /// - ZeroNetHome => lower target (min SOC)
        /// </summary>
        private async Task WriteBackSocSimulationAsync()
        {
            if (_quarterlyInfos.Count == 0)
                return;

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            double capWh = _batteryContainer.GetTotalCapacity();
            double soc = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            static double Clamp(double value, double min, double max)
                => value < min ? min : (value > max ? max : value);

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time).Where(q => q.Time >= nowQuarter))
            {
                if (!_planByTime.TryGetValue(qi.Time, out var act))
                {
                    act = new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };

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

                if (act.Mode == ChargingModes.Modes.Charging)
                {
                    if (soc + chargeStepWh > maxSocWh + NumericEpsWh)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = minSocWh;
                    }
                    else
                    {
                        soc = Math.Min(capWh, soc + chargeStepWh);
                        targetSocWh = maxSocWh;
                    }
                }
                else if (act.Mode == ChargingModes.Modes.Discharging)
                {
                    if (soc - dischargeStepWh < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = minSocWh;
                    }
                    else
                    {
                        soc = Math.Max(0.0, soc - dischargeStepWh);
                        targetSocWh = minSocWh;
                    }
                }
                else
                {
                    targetSocWh = minSocWh;
                }

                soc = Clamp(soc - netLoadWh, 0.0, capWh);

                qi.SetChargeNeeded(targetSocWh);
                qi.SetChargeLeft(soc);

                qi.SetMode(act.Mode);

                if (act.Mode == ChargingModes.Modes.Charging)
                    qi.SetPlanPower(act.PowerW > 0 ? act.PowerW : _batteryContainer.GetChargingCapacityInWattsPerHour(), 0);
                else if (act.Mode == ChargingModes.Modes.Discharging)
                    qi.SetPlanPower(0, act.PowerW > 0 ? act.PowerW : _batteryContainer.GetDischargingCapacityInWattsPerHour());
                else
                    qi.SetPlanPower(0, 0);
            }
        }

        /// <summary>
        /// Runtime-safe action for NOW.
        /// This enforces the same min/max SOC envelopes as the planner.
        /// </summary>
        private async Task<PlanAction> GetExecutableActionForNowAsync(DateTime nowQuarter)
        {
            if (!_planByTime.TryGetValue(nowQuarter, out var planned))
            {
                return new PlanAction
                {
                    Mode = ChargingModes.Modes.ZeroNetHome,
                    PowerW = 0
                };
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

            // Solar surplus should be handled by ZeroNetHome,
            // not by active grid charging.
            if (qi != null)
            {
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                if (netLoadWh < -1.0)
                {
                    return new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };
                }
            }

            if (planned.Mode == ChargingModes.Modes.Charging)
            {
                if (socWh + chargeStepWh > maxSocWh + NumericEpsWh)
                {
                    return new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };
                }

                // Only restrict active charging by price when netting is disabled.
                if (!netting && qi != null && qi.BuyingPrice > CheapGridChargeThresholdEur)
                {
                    return new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };
                }

                return planned;
            }

            if (planned.Mode == ChargingModes.Modes.Discharging)
            {
                double requiredWh = planned.PowerW > 10
                    ? planned.PowerW * 0.25
                    : dischargeStepWh;

                double socAfterDischarge = socWh - requiredWh;

                if (socAfterDischarge < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                {
                    return new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };
                }

                // Only restrict export-style discharge when netting is disabled.
                if (!netting && qi != null)
                {
                    double exportThreshold = selfUseValue + _settingsConfig.CycleCost + ExportPremiumEur;

                    if (qi.SellingPrice < exportThreshold)
                    {
                        return new PlanAction
                        {
                            Mode = ChargingModes.Modes.ZeroNetHome,
                            PowerW = 0
                        };
                    }
                }

                return planned;
            }

            return planned;
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
                    case ChargingModes.Modes.Charging:
                        if (action.PowerW > 10)
                            await _batteryContainer.StartCharging((int)Math.Round(action.PowerW)).ConfigureAwait(false);
                        else
                            await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;

                    case ChargingModes.Modes.Discharging:
                        if (action.PowerW > 10)
                            await _batteryContainer.StartDisharging((int)Math.Round(action.PowerW)).ConfigureAwait(false);
                        else
                            await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;

                    case ChargingModes.Modes.ZeroNetHome:
                        await _batteryContainer.StartNetZeroHome().ConfigureAwait(false);
                        break;

                    case ChargingModes.Modes.Disabled:
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