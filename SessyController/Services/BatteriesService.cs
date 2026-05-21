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
        private QuarterlyMeasurementDataService? _measurementService;
        private InverterMeasurementDataService? _inverterMeasurementService;
        private TaxesDataService _taxesDataService;
        private MilpService _milpService;

        // Curtailment: throttles the solar inverter when price is negative and battery is full.
        private InverterCurtailmentService _inverterCurtailmentService;

        private List<QuarterlyInfo> _quarterlyInfos = new();

        private const double FullThresholdRatio = 0.995;

        public bool IsManualOverride => _settingsConfig.ManualOverride;
        public bool WeAreInControl { get; private set; } = true;

        public delegate Task DataChangedDelegate();
        public event DataChangedDelegate? DataChanged;

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
            _measurementService = _scope.ServiceProvider.GetService<QuarterlyMeasurementDataService>();
            _inverterMeasurementService = _scope.ServiceProvider.GetService<InverterMeasurementDataService>();
            _taxesDataService = _scope.ServiceProvider.GetRequiredService<TaxesDataService>();
            _inverterCurtailmentService = _scope.ServiceProvider.GetRequiredService<InverterCurtailmentService>();
            _milpService = _scope.ServiceProvider.GetRequiredService<MilpService>();

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

                    await StoreQuarterlyMeasurement().ConfigureAwait(false);
                    await BackfillSolarProductionAsync().ConfigureAwait(false);
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

                // Delegate all MILP planning to MilpService.
                // This builds tariff context, rebuilds the plan if needed, applies
                // self-consumption policy, SOC feasibility and SOC simulation.
                await _milpService.BuildPlanAsync(_quarterlyInfos, currentSocWh).ConfigureAwait(false);

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                // Zero out solar forecast for quarters where the inverter will be shut
                // down: only during Charging quarters with negative prices.
                // SmoothedSolarPower is recalculated after zeroing so the smoothing
                // window does not pull down adjacent quarters.
                if (_settingsConfig.SolarSystemShutsDownDuringNegativePrices)
                {
                    foreach (var qi in _quarterlyInfos.Where(q => q.PriceIsNegative &&
                        q.Charging))
                    {
                        qi.SolarPowerPerQuarterHour = 0.0;
                    }

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

                if (await WeControlTheBatteries().ConfigureAwait(false))
                {
                    var (execMode, execPowerW) = await _milpService.GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

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
                        execMode = Modes.Disabled;
                        execPowerW = 0;
                    }
                    // ── End curtailment check ────────────────────────────────────────────

                    // Apply runtime override when curtailment or SOC guards changed the action.
                    // This keeps the plan and SOC simulation in sync with the actual execution.
                    var (plannedMode, plannedPowerW) = await _milpService.GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

                    if (plannedMode != execMode || Math.Abs(plannedPowerW - execPowerW) > 0.1)
                    {
                        _milpService.ApplyRuntimeOverride(nowQuarter, execMode, execPowerW);
                        await _milpService.BuildPlanAsync(_quarterlyInfos, currentSocWh).ConfigureAwait(false);
                    }

                    await ExecuteAction(execMode, execPowerW).ConfigureAwait(false);
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

        private async Task<bool> BatteryIsFullAsync()
        {
            double capWh = _batteryContainer.GetTotalCapacity();
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            return socWh >= capWh * FullThresholdRatio;
        }

        private async Task ExecuteAction(Modes mode, double powerW)
        {
#if !DEBUG
            if (_dayAheadMarketService.IsInitialized())
            {
                if (_settingsConfig.ManualOverride)
                {
                    await ExecuteManualOverride().ConfigureAwait(false);
                    return;
                }

                switch (mode)
                {
                    case Modes.Charging:
                        if (powerW > 10)
                            await _batteryContainer.StartCharging((int)Math.Round(powerW)).ConfigureAwait(false);
                        else
                            await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;

                    case Modes.Discharging:
                        if (powerW > 10)
                            await _batteryContainer.StartDisharging((int)Math.Round(powerW)).ConfigureAwait(false);
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

        private async Task BackfillSolarProductionAsync()
        {
            if (_inverterMeasurementService == null || _measurementService == null)
                return;

            // Backfill the last 2 hours of QuarterlyMeasurements with actual solar
            // from InverterMeasurements. BatteriesService may store a quarter before
            // SunspecInverterService has written the InverterMeasurement for that quarter.
            // Running this every 60s corrects those records once the data is available.
            var backfillFrom = DateTime.Now.AddHours(-2).DateFloorQuarter();
            var backfillTo = DateTime.Now.DateFloorQuarter();

            var recentMeasurements = await _measurementService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= backfillFrom && m.Time < backfillTo)
                    .OrderBy(m => m.Time)
                    .ToList();
                return await Task.FromResult(result);
            }).ConfigureAwait(false);

            foreach (var qm in recentMeasurements)
            {
                var inverterMeasurements = await _inverterMeasurementService.GetList(async set =>
                {
                    var result = set
                        .Where(m => m.Time == qm.Time)
                        .ToList();
                    return await Task.FromResult(result);
                }).ConfigureAwait(false);

                if (!inverterMeasurements.Any())
                    continue;

                var measuredSolar = inverterMeasurements.Sum(m => m.SolarProductionKWh);

                if (Math.Abs(measuredSolar - qm.SolarProductionKWh) > 0.001)
                {
                    qm.SolarProductionKWh = measuredSolar;

                    await _measurementService.Update(
                        new List<QuarterlyMeasurement> { qm },
                        (item, set) => set.FirstOrDefault(m => m.Id == item.Id))
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task StoreQuarterlyMeasurement()
        {
            if (_measurementService == null)
                return;

            var currentQuarterlyInfo = GetNextQuarterlyInfoInPlan();
            if (currentQuarterlyInfo == null)
                return;

            // Fetch battery data from the Sessy API.
            // BatteryPowerWatts: negative = charging, positive = discharging.
            var socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            var batteryPowerWatts = await _batteryContainer.GetTotalPowerInWatts().ConfigureAwait(false);

            // Use measured solar from InverterMeasurements (stored by SunspecInverterService).
            // Fall back to planned value from QuarterlyInfo if not yet available.
            double solarProductionKWh = currentQuarterlyInfo.SolarPowerPerQuarterHour;

            if (_inverterMeasurementService != null)
            {
                var inverterMeasurements = await _inverterMeasurementService.GetList(async set =>
                {
                    var result = set
                        .Where(m => m.Time == currentQuarterlyInfo.Time)
                        .ToList();
                    return await Task.FromResult(result);
                }).ConfigureAwait(false);

                if (inverterMeasurements.Any())
                    solarProductionKWh = inverterMeasurements.Sum(m => m.SolarProductionKWh);
            }

            var mode = currentQuarterlyInfo switch
            {
                { Charging: true } => BatteryMode.Charging,
                { Discharging: true } => BatteryMode.Discharging,
                { ZeroNetHome: true } => BatteryMode.ZeroNetHome,
                _ => BatteryMode.Disabled
            };

            // Update the existing QuarterlyMeasurement record created by EnergyMonitorService,
            // or create a new one if it does not exist yet (e.g. on first startup).
            var existing = await _measurementService.Get(async set =>
            {
                var result = set.FirstOrDefault(m => m.Time == currentQuarterlyInfo.Time);
                return await Task.FromResult(result);
            }).ConfigureAwait(false);

            if (existing != null)
            {
                existing.BatteryPowerWatts = batteryPowerWatts;
                existing.BatteryStateOfChargeWh = socWh;
                existing.BatteryMode = mode;
                existing.SolarProductionKWh = solarProductionKWh;
                existing.PlannedRevenueEur = currentQuarterlyInfo.Profit;

                await _measurementService.Update(
                    new List<QuarterlyMeasurement> { existing },
                    (item, set) => set.FirstOrDefault(m => m.Id == item.Id))
                    .ConfigureAwait(false);
            }
            else
            {
                // EnergyMonitorService hasn't stored a record yet — create one now.
                var measurement = new QuarterlyMeasurement
                {
                    Time = currentQuarterlyInfo.Time,
                    BatteryPowerWatts = batteryPowerWatts,
                    BatteryStateOfChargeWh = socWh,
                    BatteryMode = mode,
                    SolarProductionKWh = solarProductionKWh,
                    BuyingPriceEur = currentQuarterlyInfo.BuyingPrice,
                    SellingPriceEur = currentQuarterlyInfo.SellingPrice,
                    PlannedRevenueEur = currentQuarterlyInfo.Profit,
                };

                await _measurementService.Add(new List<QuarterlyMeasurement> { measurement })
                    .ConfigureAwait(false);
            }
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
            var (mode, _) = await _milpService.GetExecutableActionForNowAsync(now).ConfigureAwait(false);
            return ChargingModes.GetDisplayMode(mode);
        }

        public QuarterlyInfo? GetNextQuarterlyInfoInPlan()
        {
            var now = _timeZoneService.Now.DateFloorQuarter();

            return _quarterlyInfos
                .OrderBy(q => q.Time)
                .FirstOrDefault(q => q.Time >= now && _milpService.HasPlanFor(q.Time));
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

            _milpService.Dispose();
            _scope.Dispose();

            base.Dispose();
            _isDisposed = true;
        }
    }
}