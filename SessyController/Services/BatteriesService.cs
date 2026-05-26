using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyController.Services.StateMachine;
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

        private EPEXPricesService _epexPricesService;
        private SolarService _solarService;
        private BatteryContainer _batteryContainer;
        private TimeZoneService _timeZoneService;
        private ConsumptionMonitorService _consumptionMonitorService;
        private SessyWebControlDataService _sessyWebControlDataService;
        private QuarterlyMeasurementDataService? _measurementService;
        private InverterMeasurementDataService? _inverterMeasurementService;
        private TaxesDataService _taxesDataService;
        private MilpService _milpService;
        private EnergySystemStateMachine _stateMachine;
        private EnergySystemInput _systemInput;
        private HardwareStatusService _hardwareStatus;
        private ActualQuarterDataService _actualQuarterDataService;

        // Curtailment: throttles the solar inverter when price is negative.
        private InverterCurtailmentService _inverterCurtailmentService;

        private List<QuarterlyInfo> _quarterlyInfos = new();
        private bool _tombstoneRestoreAttempted = false;

        // Track the last quarter for which a snapshot was written.
        private DateTime _lastSnapshotQuarter = DateTime.MinValue;



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

            _epexPricesService = _scope.ServiceProvider.GetRequiredService<EPEXPricesService>();
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
            _hardwareStatus = _scope.ServiceProvider.GetRequiredService<HardwareStatusService>();
            _stateMachine = _scope.ServiceProvider.GetRequiredService<EnergySystemStateMachine>();
            _actualQuarterDataService = _scope.ServiceProvider.GetRequiredService<ActualQuarterDataService>();
            _systemInput = new EnergySystemInput(
                _hardwareStatus,
                _milpService,
                this,
                _timeZoneService,
                _scope.ServiceProvider.GetRequiredService<ILogger<EnergySystemInput>>());

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

                    // Run every second when subscribers are listening (UI refresh),
                    // otherwise every 60 seconds to reduce idle CPU load.
                    delaySeconds = DataChanged != null ? 1 : 60;

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
            if (_epexPricesService == null || !_epexPricesService.IsInitialized())
                return;

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!await RefreshQuarterlyPrices().ConfigureAwait(false))
                    return;

                // On first run after startup: attempt to restore the tombstoned plan.
                // _quarterlyInfos is now populated so the price signature can be compared.
                if (!_tombstoneRestoreAttempted)
                {
                    _tombstoneRestoreAttempted = true;
                    await _milpService.TryRestorePlanAsync().ConfigureAwait(false);
                }

                await _consumptionMonitorService.EstimateConsumptionInWattsPerQuarter(_quarterlyInfos).ConfigureAwait(false);
                await _solarService.GetExpectedSolarPower(_quarterlyInfos).ConfigureAwait(false);

                // SOC via HardwareStatusService (polled in background — no extra Sessy request).
                // Do not proceed with plan building until hardware status is available —
                // a SOC of 0 would cause a false deviation and destroy the tombstoned plan.
                if (!_hardwareStatus.IsReady)
                {
                    _logger.LogWarning("BatteriesService: HardwareStatusService not ready yet — skipping cycle.");
                    return;
                }

                double currentSocWh = _hardwareStatus.CurrentSocWh;

                // Delegate all MILP planning to MilpService.
                await _milpService.BuildPlanAsync(_quarterlyInfos, currentSocWh).ConfigureAwait(false);

                if (await WeControlTheBatteries().ConfigureAwait(false))
                {
                    await _systemInput.LoadAsync().ConfigureAwait(false);

                    // Evaluate state machine — all decisions made here.
                    var action = _stateMachine.Evaluate(_systemInput);

                    // Write actual quarter record once per quarter.
                    await WriteActualQuarterIfNewAsync(_systemInput, action).ConfigureAwait(false);

                    // ── Execute ───────────────────────────────────────────────────────
                    await ExecuteAction(action.BatteryMode, action.BatterySetpointW).ConfigureAwait(false);
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
            var prices = await _epexPricesService.GetPrices().ConfigureAwait(false);

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

        /// <summary>
        /// Writes an ActualQuarter record once per quarter at the start of each new quarter.
        /// Captures actual hardware state vs state machine decision for plan vs actual analysis.
        /// </summary>
        private async Task WriteActualQuarterIfNewAsync(
            EnergySystemInput input,
            SessyController.Services.StateMachine.EnergySystemAction action)
        {
            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            if (nowQuarter <= _lastSnapshotQuarter)
                return;

            try
            {
                var actual = new SessyData.Model.ActualQuarter
                {
                    Time = nowQuarter,
                    // Store the state machine's battery mode (Charging/Discharging/ZeroNetHome/Disabled)
                    // so ModeMatch in PlanVsActualService can compare it to PlannedMode directly.
                    // The raw hardware strategy (POWER_STRATEGY_API/NOM) is intentionally not used here.
                    ActualMode = action.BatteryMode.ToString(),
                    ActualPowerW = input.ActualBatteryPowerW,
                    ActualSocWh = input.ActualSocWh,
                    CurtailmentMode = action.CurtailmentMode.ToString(),
                    StateMachineReason = action.Reason
                };

                await _actualQuarterDataService
                    .AddOrUpdate(new List<ActualQuarter> { actual }, (item, set) => set.FirstOrDefault(q => q.Time == item.Time))
                    .ConfigureAwait(false);

                _lastSnapshotQuarter = nowQuarter;

                _logger.LogInformation(
                    $"ActualQuarter written for {nowQuarter:dd-MM HH:mm} — " +
                    $"SOC={input.ActualSocWh:F0}Wh Mode={action.BatteryMode} " +
                    $"Curtailment={action.CurtailmentMode}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"WriteActualQuarterIfNewAsync failed: {ex.Message}");
            }
        }

        private async Task ExecuteAction(Modes mode, double powerW)
        {
#if !DEBUG
            if (_epexPricesService.IsInitialized())
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
            try
            {
                foreach (var battery in _batteryContainer.Batteries ?? [])
                {
                    var currentPowerStrategy = await battery.GetPowerStatus().ConfigureAwait(false);
                    if (currentPowerStrategy?.Sessy?.StrategyOverridden == true)
                        return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"SupplierIsControllingTheBatteries: could not reach battery — assuming not overridden. {ex.Message}");
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
            var backfillFrom = _timeZoneService.Now.AddHours(-2).DateFloorQuarter();
            var backfillTo = _timeZoneService.Now.DateFloorQuarter();

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

            // Use the actually executed mode, not the planned mode from QuarterlyInfo.
            // The runtime may have overridden the plan (e.g. SOC guard → NZH, curtailment → Disabled).
            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();
            var (executedMode, _) = await _milpService.GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

            var mode = executedMode switch
            {
                Modes.Charging => BatteryMode.Charging,
                Modes.Discharging => BatteryMode.Discharging,
                Modes.ZeroNetHome => BatteryMode.ZeroNetHome,
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
                // EnergyMonitorService may have stored a record concurrently.
                // Use AddOrUpdate to avoid UNIQUE constraint violations.
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

                await _measurementService.AddOrUpdate(
                    new List<QuarterlyMeasurement> { measurement },
                    (item, set) => set.FirstOrDefault(m => m.Time == item.Time))
                    .ConfigureAwait(false);
            }
        }

        public List<QuarterlyInfo> GetQuarterlyInfos()
        {
            // Note: intentionally no semaphore here.
            // This method is called from EnergySystemInput.LoadAsync() which is
            // called from within Process() — which already holds the semaphore.
            // Taking it again would deadlock.
            return _quarterlyInfos.ToList();
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