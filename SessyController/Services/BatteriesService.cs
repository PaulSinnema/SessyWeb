using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;
using static SessyData.Model.SessyWebControl;

namespace SessyController.Services
{
    /// <summary>
    /// Simplified battery controller.
    ///
    /// Design goals:
    /// - maximize economic arbitrage
    /// - keep enough energy for upcoming expensive periods
    /// - avoid draining the battery too early
    /// - keep only a modest amount of solar headroom
    /// - keep runtime safety checks simple
    ///
    /// This version intentionally avoids layered post-processing logic.
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

        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _minSocWhByTime = new();
        private Dictionary<DateTime, double> _maxSocWhByTime = new();

        public bool IsManualOverride => _settingsConfig.ManualOverride;
        public bool WeAreInControl { get; private set; } = true;

        public delegate Task DataChangedDelegate();
        public event DataChangedDelegate? DataChanged;

        private sealed record PlanAction
        {
            public Modes Mode;
            public double PowerW;
        }

        // Runtime safety
        private const double ReserveWh = 0.0;
        private const double EmptyHysteresisWh = 250.0;
        private const double FullThresholdRatio = 0.995;
        private const double NumericEpsWh = 0.001;

        // Planning behavior
        private const int ReserveLookAheadQuarters = 32;          // 8 hours
        private const int SolarHeadroomLookAheadQuarters = 12;    // 3 hours
        private const double ReserveSafetyFactor = 1.10;
        private const double NomReserveWh = 750.0;
        private const double ExpensiveToleranceEur = 0.08;
        private const double CheapToleranceEur = 0.02;
        private const double StrongArbitrageSpreadEur = 0.15;
        private const double PeakToleranceEur = 0.03;
        private const double MaxSolarHeadroomRatio = 0.20;
        private const double MinChargeSpreadOverCycleCostEur = 0.02;
        private const double SolarHeadroomSafetyFactor = 1.05;

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
                await BuildTariffContextAsync().ConfigureAwait(false);
                await BuildSimpleEconomicPlanAsync().ConfigureAwait(false);
                await WriteBackSocSimulationAsync().ConfigureAwait(false);

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                if (await WeControlTheBatteries().ConfigureAwait(false))
                {
                    var executable = await GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

                    if (!_planByTime.TryGetValue(nowQuarter, out var plannedNow) ||
                        plannedNow.Mode != executable.Mode ||
                        Math.Abs(plannedNow.PowerW - executable.PowerW) > 0.1)
                    {
                        ApplyRuntimeOverrideToPlan(nowQuarter, executable);
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
        /// Build a simple future reserve and maximum charging envelope.
        /// The reserve protects upcoming expensive quarters.
        /// The maximum SOC only keeps modest short-term solar headroom.
        /// </summary>
        private async Task BuildTariffContextAsync()
        {
            _nettingByTime.Clear();
            _minSocWhByTime.Clear();
            _maxSocWhByTime.Clear();

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

            double capacityWh = _batteryContainer.GetTotalCapacity();

            static double Clamp(double value, double min, double max)
                => value < min ? min : (value > max ? max : value);

            for (int i = 0; i < ordered.Count; i++)
            {
                var qi = ordered[i];

                var futureReserveWindow = ordered
                    .Skip(i + 1)
                    .Take(ReserveLookAheadQuarters)
                    .ToList();

                if (futureReserveWindow.Count == 0)
                {
                    _minSocWhByTime[qi.Time] = ReserveWh;
                    _maxSocWhByTime[qi.Time] = capacityWh;
                    continue;
                }

                // Reserve enough energy for expensive future positive net-load quarters.
                double reserveDemandWh = futureReserveWindow
                    .Where(f => f.BuyingPrice >= qi.BuyingPrice + ExpensiveToleranceEur)
                    .Select(f => Math.Max(0.0, f.EstimatedConsumptionPerQuarterInWatts - f.SolarPowerPerQuarterInWatts))
                    .Sum();

                double minSocWh = reserveDemandWh * ReserveSafetyFactor + NomReserveWh;
                minSocWh = Clamp(minSocWh, ReserveWh, capacityWh);

                // Strong future arbitrage means we do not want solar headroom to block charging.
                double futureMaxBuy = futureReserveWindow.Max(f => f.BuyingPrice);
                bool strongArbitrageComing = futureMaxBuy >= qi.BuyingPrice + StrongArbitrageSpreadEur;

                // Keep only a modest amount of near-term solar headroom.
                double solarHeadroomWh = 0.0;

                if (!strongArbitrageComing && qi.BuyingPrice >= 0.0)
                {
                    var solarWindow = ordered
                        .Skip(i + 1)
                        .Take(SolarHeadroomLookAheadQuarters)
                        .ToList();

                    solarHeadroomWh = solarWindow
                        .Select(f => Math.Max(0.0, f.SolarPowerPerQuarterInWatts - f.EstimatedConsumptionPerQuarterInWatts))
                        .Sum();

                    solarHeadroomWh *= SolarHeadroomSafetyFactor;
                    solarHeadroomWh = Math.Min(solarHeadroomWh, capacityWh * MaxSolarHeadroomRatio);
                }

                double maxSocWh = capacityWh - solarHeadroomWh;

                // Negative prices must be allowed to fill the battery completely.
                if (qi.BuyingPrice < 0.0)
                    maxSocWh = capacityWh;

                maxSocWh = Clamp(maxSocWh, ReserveWh, capacityWh);

                if (maxSocWh < minSocWh)
                    maxSocWh = minSocWh;

                _minSocWhByTime[qi.Time] = minSocWh;
                _maxSocWhByTime[qi.Time] = maxSocWh;
            }
        }

        /// <summary>
        /// Build a simple greedy economic plan:
        /// - charge when current buy price is cheap compared to future best sell/buy value
        /// - discharge only near price peaks and never below required reserve
        /// - otherwise run ZeroNetHome
        /// </summary>
        private async Task BuildSimpleEconomicPlanAsync()
        {
            _planByTime.Clear();

            if (_quarterlyInfos.Count == 0)
                return;

            var ordered = _quarterlyInfos
                .OrderBy(q => q.Time)
                .ToList();

            double capacityWh = _batteryContainer.GetTotalCapacity();
            double maxChargePowerW = _batteryContainer.GetChargingCapacityInWattsPerHour();
            double maxDischargePowerW = _batteryContainer.GetDischargingCapacityInWattsPerHour();
            double maxChargeStepWh = maxChargePowerW / 4.0;
            double maxDischargeStepWh = maxDischargePowerW / 4.0;
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            for (int i = 0; i < ordered.Count; i++)
            {
                var qi = ordered[i];

                // Keep historical quarters passive.
                if (qi.Time < nowQuarter)
                {
                    _planByTime[qi.Time] = new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };
                    continue;
                }

                bool netting = _nettingByTime.TryGetValue(qi.Time, out var n) ? n : true;
                double minSocWh = _minSocWhByTime.TryGetValue(qi.Time, out var minSoc) ? minSoc : ReserveWh;
                double maxSocWh = _maxSocWhByTime.TryGetValue(qi.Time, out var maxSoc) ? maxSoc : capacityWh;
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                var future = ordered.Skip(i + 1).ToList();

                double futureBestValue = netting
                    ? (future.Count > 0 ? future.Max(f => f.SellingPrice) : qi.SellingPrice)
                    : (future.Count > 0 ? future.Max(f => f.BuyingPrice) : qi.BuyingPrice);

                double futurePeakSell = future.Count > 0 ? future.Max(f => f.SellingPrice) : qi.SellingPrice;
                double futurePeakBuy = future.Count > 0 ? future.Max(f => f.BuyingPrice) : qi.BuyingPrice;
                double requiredChargeSpread = _settingsConfig.CycleCost + MinChargeSpreadOverCycleCostEur;

                bool roomToCharge = socWh + maxChargeStepWh <= maxSocWh + NumericEpsWh;
                bool roomToDischarge = socWh - maxDischargeStepWh >= minSocWh + EmptyHysteresisWh + NumericEpsWh;

                bool isNegativePrice = qi.BuyingPrice < 0.0;
                bool cheapVersusFuture = futureBestValue - qi.BuyingPrice >= requiredChargeSpread;
                bool nearFuturePeak = qi.SellingPrice >= futurePeakSell - PeakToleranceEur;
                bool clearlyExpensive = qi.BuyingPrice >= futurePeakBuy - PeakToleranceEur;

                PlanAction action;

                // Charge aggressively at negative prices or when there is strong future arbitrage.
                if (roomToCharge && (isNegativePrice || cheapVersusFuture))
                {
                    action = new PlanAction
                    {
                        Mode = ChargingModes.Modes.Charging,
                        PowerW = maxChargePowerW
                    };
                }
                // Discharge only when current value is near a peak and reserve allows it.
                else if (roomToDischarge && (nearFuturePeak || clearlyExpensive))
                {
                    action = new PlanAction
                    {
                        Mode = ChargingModes.Modes.Discharging,
                        PowerW = maxDischargePowerW
                    };
                }
                else
                {
                    action = new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };
                }

                _planByTime[qi.Time] = action;

                // Simulate forward to keep planning state coherent.
                if (action.Mode == ChargingModes.Modes.Charging)
                {
                    double chargeWh = action.PowerW * 0.25;
                    socWh = Math.Min(capacityWh, socWh + chargeWh);
                }
                else if (action.Mode == ChargingModes.Modes.Discharging)
                {
                    double dischargeWh = action.PowerW * 0.25;
                    socWh = Math.Max(0.0, socWh - dischargeWh);
                }

                // Apply household effect after the planned battery action.
                socWh -= netLoadWh;
                socWh = Math.Max(0.0, Math.Min(capacityWh, socWh));
            }

            WritePlanIntoQuarterlyInfos();
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
        /// Simulate forward from current SOC for charting and diagnostics.
        /// </summary>
        private async Task WriteBackSocSimulationAsync()
        {
            if (_quarterlyInfos.Count == 0)
                return;

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            double capWh = _batteryContainer.GetTotalCapacity();
            double soc = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            double maxChargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double maxDischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            static double Clamp(double value, double min, double max)
                => value < min ? min : (value > max ? max : value);

            foreach (var qi in _quarterlyInfos
                .OrderBy(q => q.Time)
                .Where(q => q.Time >= nowQuarter))
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
                    double plannedChargeWh = act.PowerW > 10
                        ? act.PowerW * 0.25
                        : maxChargeStepWh;

                    plannedChargeWh = Math.Min(plannedChargeWh, maxChargeStepWh);

                    if (soc + plannedChargeWh > maxSocWh + NumericEpsWh)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = minSocWh;
                    }
                    else
                    {
                        soc = Math.Min(capWh, soc + plannedChargeWh);
                        targetSocWh = maxSocWh;
                    }
                }
                else if (act.Mode == ChargingModes.Modes.Discharging)
                {
                    double plannedDischargeWh = act.PowerW > 10
                        ? act.PowerW * 0.25
                        : maxDischargeStepWh;

                    plannedDischargeWh = Math.Min(plannedDischargeWh, maxDischargeStepWh);

                    if (soc - plannedDischargeWh < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = minSocWh;
                    }
                    else
                    {
                        soc = Math.Max(0.0, soc - plannedDischargeWh);
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
        /// Runtime guard.
        /// Keep this simple:
        /// - do not charge above max
        /// - do not discharge below min
        /// - otherwise trust the plan
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

            double minSocWh = _minSocWhByTime.TryGetValue(nowQuarter, out var minSoc)
                ? minSoc
                : ReserveWh;

            double maxSocWh = _maxSocWhByTime.TryGetValue(nowQuarter, out var maxSoc)
                ? maxSoc
                : capWh;

            maxSocWh = Math.Min(maxSocWh, fullThresholdWh);

            if (planned.Mode == ChargingModes.Modes.Charging)
            {
                double requiredWh = planned.PowerW > 10
                    ? planned.PowerW * 0.25
                    : chargeStepWh;

                if (socWh + requiredWh > maxSocWh + NumericEpsWh)
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

                if (socWh - requiredWh < minSocWh + EmptyHysteresisWh + NumericEpsWh)
                {
                    return new PlanAction
                    {
                        Mode = ChargingModes.Modes.ZeroNetHome,
                        PowerW = 0
                    };
                }

                return planned;
            }

            return planned;
        }

        private void ApplyRuntimeOverrideToPlan(DateTime time, PlanAction action)
        {
            _planByTime[time] = action;
            WritePlanIntoQuarterlyInfos();
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

            _scope.Dispose();

            base.Dispose();
            _isDisposed = true;
        }
    }
}