using Microsoft.Extensions.Options;
using SessyCommon.Enums;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Interfaces;
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

        private readonly IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;

        private IDisposable? _sessyBatteryConfigSubscription;

        private SettingsService _settingsService;
        private Settings _settingsConfig;
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
        private IMilpService _milpService;
        private EnergySystemStateMachine _stateMachine;
        private EnergySystemInput _systemInput;
        private HardwareStatusService _hardwareStatus;
        private ActualQuarterDataService _actualQuarterDataService;
        private PlannerLearningService _plannerLearningService;
        private ChargedScheduleService _chargedScheduleService;
        private BatteryEfficiencyService _batteryEfficiencyService;
        private ControlModeService _controlMode;

        // Curtailment: throttles the solar inverter when price is negative.
        private InverterCurtailmentService _inverterCurtailmentService;

        private List<QuarterlyInfo> _quarterlyInfos { get; set; } = new();
        private bool _tombstoneRestoreAttempted = false;

        // Track the last quarter for which a snapshot was written.
        private DateTime _lastSnapshotQuarter = DateTime.MinValue;

        private const double FullThresholdRatio = 0.995;

        public bool IsManualOverride => _controlMode.Current == ControlMode.Manual;
        public bool WeAreInControl => _controlMode.WeMayDriveTheBatteries;
        public bool ChargedInControl => _controlMode.Current == ControlMode.Charged;

        public delegate Task DataChangedDelegate();
        public event DataChangedDelegate? DataChanged;

        public BatteriesService(
            LoggingService<BatteriesService> logger,
            SettingsService settingsService,
            IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;

            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;

            _settingsService = settingsService;
            _settingsConfig = _settingsService.Current;
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue ?? throw new InvalidOperationException("Sessy:Batteries missing");

            _settingsService.SettingsChanged += (s, _) => _settingsConfig = s;
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
            _milpService = _scope.ServiceProvider.GetRequiredService<IMilpService>();
            _hardwareStatus = _scope.ServiceProvider.GetRequiredService<HardwareStatusService>();
            _stateMachine = _scope.ServiceProvider.GetRequiredService<EnergySystemStateMachine>();
            _actualQuarterDataService = _scope.ServiceProvider.GetRequiredService<ActualQuarterDataService>();
            _plannerLearningService = _scope.ServiceProvider.GetRequiredService<PlannerLearningService>();
            _chargedScheduleService = _scope.ServiceProvider.GetRequiredService<ChargedScheduleService>();
            _batteryEfficiencyService = _scope.ServiceProvider.GetRequiredService<BatteryEfficiencyService>();
            _controlMode = _scope.ServiceProvider.GetRequiredService<ControlModeService>();
            _systemInput = new EnergySystemInput(
                _hardwareStatus,
                _milpService,
                this,
                _timeZoneService);

            _logger.LogInformation("BatteriesService starting");
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _scope.Dispose();
            return base.StopAsync(cancellationToken);
        }

        // True once Process has actually built a plan (not bailed out on a not-yet-ready
        // dependency). Used to retry quickly on a cold start until the first plan exists.
        private bool _planBuiltThisCycle;

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("BatteriesService started ...");

            // Wait until SettingsService has loaded settings from the database.
            await _settingsService.WaitForReadyAsync().ConfigureAwait(false);
            _settingsConfig = _settingsService.Current;

            var delaySeconds = 60;
#if DEBUG
            delaySeconds = 5;
#endif

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await HeartBeatAsync().ConfigureAwait(false);

                    // Returns immediately unless today's nightly fit is still due.
                    await _plannerLearningService.EnsureLearnedAsync().ConfigureAwait(false);

                    await Process(cancellationToken).ConfigureAwait(false);

                    // The control rate must not depend on whether a browser has the charging hours
                    // page open — DataChanged is subscribed there and nowhere else, so this ran
                    // once per second on every unattended installation and turned each threshold
                    // flicker into a strategy write per second. The UI gets an immediate cycle
                    // through OnHeartBeat; it does not need a faster loop.
#if DEBUG
                    delaySeconds = _planBuiltThisCycle ? 10 : 2;
#else
                    delaySeconds = _planBuiltThisCycle ? 60 : 5;
#endif

                    if (DataChanged != null)
                        await DataChanged.Invoke().ConfigureAwait(false);

                    await StoreQuarterlyMeasurement().ConfigureAwait(false);
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

        // Sets _planBuiltThisCycle: true when a plan was actually built this cycle, false when it bailed out early
        // because a dependency (EPEX prices, hardware SOC) was not ready yet. The caller uses that
        // to retry quickly on a cold start instead of waiting a full control interval.
        public async Task Process(CancellationToken cancellationToken)
        {
            _planBuiltThisCycle = false;

            if (_epexPricesService == null || !_epexPricesService.IsInitialized())
            {
                _logger.LogInformation("BatteriesService: EPEX prices not yet initialized — retrying in 2s.");
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return;
            }

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

                // Solar forecast is updated every cycle unconditionally — before BuildPlanAsync.
                // Because _quarterlyInfos is passed by reference into MilpService, the updated
                // SolarPowerPerQuarterHour values are always visible to the solver regardless
                // of whether the resulting plan is committed or rejected (speculative solve).
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
                _planBuiltThisCycle = true;

                // What the batteries plan for themselves, whoever is executing: under Charged it is
                // what will happen, under our own control it is the alternative we did not take.
                // Runs after BuildPlanAsync, which rewrites the QuarterlyInfos every cycle.
                // A plain read, so unlike the handover below it also runs in a debug session.
                await ApplyChargedScheduleAsync().ConfigureAwait(false);

                var weDrive = await WeControlTheBatteries().ConfigureAwait(false);

                // Evaluating runs in every control mode, executing does not. The solar inverter is
                // our hardware whoever drives the batteries, so curtailment must keep working —
                // InverterCurtailmentService acts on CurrentAction every 5 seconds and would
                // otherwise stay frozen on the last action from before the handover. Writing the
                // ActualQuarter here keeps plan-vs-actual covered over those periods too.
                await _systemInput.LoadAsync().ConfigureAwait(false);

                var action = _stateMachine.Evaluate(_systemInput);

                await WriteActualQuarterIfNewAsync(_systemInput, action).ConfigureAwait(false);

                if (weDrive)
                {
                    // ── Execute ───────────────────────────────────────────────────────
                    await ExecuteAction(action.BatteryMode, action.BatterySetpointW).ConfigureAwait(false);

                    WatchStrategy(_systemInput.NowQuarter, action.BatteryMode);
                }
                else
                {
#if !DEBUG
                    // Handing over to Charged: put the batteries on the Sessy strategy that matches
                    // ours. Once, on the transition — re-asserting it every cycle would overwrite a
                    // strategy set by hand in the Sessy portal.
                    if (_controlMode.JustHandedOverToCharged)
                        await HandOverToChargedAsync().ConfigureAwait(false);

                    // For provider (supplier) control we release the batteries fully. Under Charged
                    // we leave them alone after the handover — they are running their own plan.
                    if (!ChargedInControl)
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
                    ActualMode = _hardwareStatus.ActualBatteryStrategy,
                    ActualPowerW = input.ActualBatteryPowerW,
                    ActualSocWh = input.ActualSocWh,
                    CurtailmentMode = action.CurtailmentMode.ToString(),
                    StateMachineReason = action.Reason,
                    ControlMode = _controlMode.Current.ToString()
                };

                await _actualQuarterDataService
                    .AddOrUpdate(new List<ActualQuarter> { actual }, (item, set) => set.FirstOrDefault(q => q.Time == item.Time))
                    .ConfigureAwait(false);

                _lastSnapshotQuarter = nowQuarter;

                _logger.LogInformation(
                    $"ActualQuarter written for {nowQuarter:dd-MM HH:mm} — " +
                    $"SOC={input.ActualSocWh:F0}Wh Mode={action.BatteryMode} " +
                    $"Curtailment={action.CurtailmentMode} Control={_controlMode.Current}");
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
                    // (Dis)charging now runs through NOM; the P1 grid target (GridTargetService) sets
                    // the power. StartCharging/StartDisharging stay as dead code, no longer called.
                    case Modes.Charging:
                    case Modes.Discharging:
                        await _batteryContainer.StartNetZeroHome().ConfigureAwait(false);
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

        // ── Strategy watch ───────────────────────────────────────────────────
        //
        // Both checks below exist for problems reported from installations we cannot inspect. The
        // production log level is Warning, so a path that logs at Information is a path that says
        // nothing; these are the two symptoms a user can actually see in the Sessy app or in Home
        // Assistant, and neither of them was visible in the log. Both are gated: once per quarter
        // for the churn, once per episode for the foreign strategy — never per cycle.

        /// <summary>How many mode changes inside one quarter are no longer normal planning.</summary>
        private const int StrategyChurnThreshold = 4;

        /// <summary>How many consecutive cycles the hardware may disagree before it is reported.</summary>
        private const int ForeignStrategyCycles = 3;

        private DateTime _strategyWatchQuarter = DateTime.MinValue;
        private Modes _lastCommandedMode = Modes.Unknown;
        private int _modeChangesThisQuarter;
        private bool _churnReported;
        private int _foreignStrategyCycles;
        private bool _foreignStrategyReported;

        /// <summary>
        /// Evidence for the two symptoms above, read by ConfigurationCheckService. Counting them is
        /// the point: the log is not visible in the UI, and "the battery keeps switching" is exactly
        /// the report that arrives without any way to say why. Same pattern as
        /// ConsumptionMonitorService.NegativeConsumptionQuarters.
        /// </summary>
        public int StrategyChurnQuarters { get; private set; }
        public DateTime? LastStrategyChurnAt { get; private set; }
        public int LastStrategyChurnCount { get; private set; }

        public int ForeignStrategyEpisodes { get; private set; }
        public DateTime? LastForeignStrategyAt { get; private set; }
        public string? LastForeignStrategy { get; private set; }
        public string? LastCommandedStrategy { get; private set; }

        /// <summary>
        /// The Sessy power strategy a mode is executed as. Charging, Discharging and ZeroNetHome all
        /// run through NOM now — the P1 grid target sets the power. Only Disabled goes through the
        /// open API (setpoint 0), so that boundary is the only one that rewrites the strategy.
        /// </summary>
        internal static string ExpectedStrategy(Modes mode) =>
            mode == Modes.Disabled
                ? ActivePowerStrategy.PowerStrategies.POWER_STRATEGY_API.ToString()
                : ActivePowerStrategy.PowerStrategies.POWER_STRATEGY_NOM.ToString();

        /// <summary>
        /// Reports two things that are invisible from the outside: a mode that keeps changing inside
        /// one quarter, and a battery that is not on the strategy we commanded.
        /// </summary>
        private void WatchStrategy(DateTime nowQuarter, Modes commandedMode)
        {
            if (nowQuarter != _strategyWatchQuarter)
            {
                _strategyWatchQuarter = nowQuarter;
                _modeChangesThisQuarter = 0;
                _churnReported = false;
            }

            if (_lastCommandedMode != Modes.Unknown && commandedMode != _lastCommandedMode)
                _modeChangesThisQuarter++;

            _lastCommandedMode = commandedMode;

            if (!_churnReported && _modeChangesThisQuarter >= StrategyChurnThreshold)
            {
                _churnReported = true;

                StrategyChurnQuarters++;
                LastStrategyChurnAt = nowQuarter;
                LastStrategyChurnCount = _modeChangesThisQuarter;

                _logger.LogWarning(
                    $"STRATEGY_CHURN[{nowQuarter:dd-MM HH:mm}]: battery mode changed {_modeChangesThisQuarter} times " +
                    $"in this quarter (now {commandedMode} = {ExpectedStrategy(commandedMode)}). Each change re-aims the " +
                    $"P1 grid target, and crossing to or from Disabled also rewrites the Sessy strategy. Look for a GUARD_ " +
                    $"or PLANNED_MODE_ line just above; if there is none, something outside SessyWeb is driving as well.");
            }
            else if (_churnReported)
            {
                // Keep the number honest while the quarter is still running — the warning is
                // written once, but Tips & Checks shows the final count.
                LastStrategyChurnCount = _modeChangesThisQuarter;
            }

            // The hardware strategy is polled every 10s and cached for 2s inside Battery, so one
            // disagreeing cycle right after a change means nothing. Several in a row do.
            var actual = _hardwareStatus.ActualBatteryStrategy;

            if (string.IsNullOrEmpty(actual))
                return;

            var expected = ExpectedStrategy(commandedMode);

            if (actual == expected)
            {
                if (_foreignStrategyReported)
                    _logger.LogWarning(
                        $"FOREIGN_STRATEGY[{nowQuarter:dd-MM HH:mm}]: battery is back on {actual}, as commanded.");

                _foreignStrategyCycles = 0;
                _foreignStrategyReported = false;
                return;
            }

            _foreignStrategyCycles++;

            if (_foreignStrategyReported || _foreignStrategyCycles < ForeignStrategyCycles)
                return;

            _foreignStrategyReported = true;

            ForeignStrategyEpisodes++;
            LastForeignStrategyAt = nowQuarter;
            LastForeignStrategy = actual;
            LastCommandedStrategy = expected;

            _logger.LogWarning(
                $"FOREIGN_STRATEGY[{nowQuarter:dd-MM HH:mm}]: commanded {commandedMode} ({expected}) but the battery " +
                $"reports {actual} for {_foreignStrategyCycles} cycles. SessyWeb only writes the strategy when it " +
                $"differs from what the battery reports, so it will not fight this — check for a second controller " +
                $"(Home Assistant, the Sessy app, Charged) writing the power strategy.");
        }

        private async Task ExecuteManualOverride()
        {
#if !DEBUG
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHoursArray.Contains(localTime.Hour))
                await _batteryContainer.StartCharging(_batteryContainer.GetChargingCapacityInWattsPerHour()).ConfigureAwait(false);
            else if (_settingsConfig.ManualDischargingHoursArray.Contains(localTime.Hour))
                await _batteryContainer.StartDisharging(_batteryContainer.GetDischargingCapacityInWattsPerHour()).ConfigureAwait(false);
            else if (_settingsConfig.ManualNetZeroHomeHoursArray.Contains(localTime.Hour))
                await _batteryContainer.StartNetZeroHome().ConfigureAwait(false);
            else
                await _batteryContainer.StopAll().ConfigureAwait(false);
#else
            await Task.Delay(1).ConfigureAwait(false);
#endif
        }

        /// <summary>
        /// Maps our optimisation strategy onto Sessy's own when control moves to Charged:
        ///   ProfitMaximization → ROI (Dynamic)
        ///   anything else      → ECO
        /// Balanced is ECO by agreement; SelfConsumption and BatterySaving follow it because ECO is
        /// the self-consumption-oriented, lower-cycle mode of the two Sessy offers.
        /// </summary>
        internal static bool MapsToRoi(OptimizationStrategy strategy)
            => strategy == OptimizationStrategy.ProfitMaximization;

        private async Task HandOverToChargedAsync()
        {
            try
            {
                var strategy = _settingsConfig.Strategy;

                if (MapsToRoi(strategy))
                {
                    _logger.LogWarning($"Handing over to Charged: {strategy} → ROI (Dynamic).");
                    await _batteryContainer.StartRoi().ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning($"Handing over to Charged: {strategy} → ECO.");
                    await _batteryContainer.StartEco().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // A failed handover must not take the cycle down; the batteries keep their previous
                // strategy and the user can set it in the Sessy portal.
                _logger.LogError($"Handing over to Charged failed: {ex.ToDetailedString()}");
            }
        }

        private async Task<bool> WeControlTheBatteries()
        {
            // Only this service knows whether the supplier took over; everyone else reads the mode.
            var supplierInControl = await SupplierIsControllingTheBatteries().ConfigureAwait(false);

            var mode = _controlMode.Update(supplierInControl);

            // Manual override is still SessyWeb driving, so the history keeps its old meaning.
            var status = mode switch
            {
                ControlMode.Provider => SessyWebControlStatus.Provider,
                ControlMode.Charged => SessyWebControlStatus.Charged,
                _ => SessyWebControlStatus.SessyWeb
            };

            var last = await _sessyWebControlDataService.Get(async set =>
            {
                var result = set.OrderByDescending(sc => sc.Time).FirstOrDefault();
                return await Task.FromResult(result).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (last == null || last.Status != status)
                await StoreStatus(status).ConfigureAwait(false);

            return _controlMode.WeMayDriveTheBatteries;
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

        private async Task StoreQuarterlyMeasurement()
        {
            if (_measurementService == null)
                return;

            // A measurement belongs to the quarter that is running NOW. Never use
            // GetNextQuarterlyInfoInPlan() here — that is a UI helper returning the upcoming
            // quarter, and writing a row for a future quarter makes EnergyMonitorService skip
            // that quarter (its Exists() check) so the meter readings are never stored.
            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            var currentQuarterlyInfo = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);
            if (currentQuarterlyInfo == null)
                return;

            // Fetch battery data from the Sessy API.
            // BatteryPowerWatts: negative = charging, positive = discharging.
            var socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            var batteryPowerWatts = await _batteryContainer.GetTotalPowerInWatts().ConfigureAwait(false);

            // Use the actually executed mode, not the planned mode from QuarterlyInfo.
            // The runtime may have overridden the plan (e.g. SOC guard → NZH, curtailment → Disabled).
            var (mode, _) = await _milpService.GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

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

            if (ChargedInControl)
            {
                // When Charged is in control the next action comes from their schedule, not from
                // our plan — a quarter counts when their schedule puts power on it.
                return _quarterlyInfos
                    .OrderBy(q => q.Time)
                    .FirstOrDefault(q => q.Time > now && Math.Abs(q.ChargedPlanPowerW) > 10.0);
            }

            return _quarterlyInfos
                .OrderBy(q => q.Time)
                .FirstOrDefault(q => q.Time > now && _milpService.HasPlanFor(q.Time));
        }

        /// <summary>
        /// Reads what the batteries plan for themselves and writes it onto the QuarterlyInfo
        /// objects, next to our own plan. Whichever of the two is not executing is drawn as a
        /// shadow on the chart, so the difference between the two strategies is visible either way.
        ///
        /// Our own plan fields are deliberately left alone: they feed the planner, the database and
        /// the reporting, and overwriting them (as this method used to do) made every consumer
        /// believe the MILP had produced Charged's numbers.
        /// </summary>
        private async Task ApplyChargedScheduleAsync()
        {
            try
            {
                var powerByQuarter = await _chargedScheduleService.RefreshAsync().ConfigureAwait(false);

                if (powerByQuarter.Count == 0) return;

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();
                var socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
                var (chargeEfficiency, dischargeEfficiency) =
                    await _batteryEfficiencyService.GetEfficienciesAsync().ConfigureAwait(false);

                var socByQuarter = ChargedScheduleService.Project(
                    powerByQuarter,
                    nowQuarter,
                    socWh,
                    _batteryContainer.GetTotalCapacity(),
                    chargeEfficiency,
                    dischargeEfficiency);

                foreach (var qi in _quarterlyInfos)
                {
                    if (!powerByQuarter.TryGetValue(qi.Time, out var powerW)) continue;

                    socByQuarter.TryGetValue(qi.Time, out var chargeLeftWh);

                    qi.SetChargedPlan(powerW, chargeLeftWh);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"ApplyChargedScheduleAsync failed: {ex.ToDetailedString()}");
            }
        }

        public async Task<double> getBatteryPercentage()
        {
            return await _batteryContainer.GetBatterPercentage().ConfigureAwait(false);
        }

        private bool _isDisposed;

        public override void Dispose()
        {
            if (_isDisposed) return;

            _sessyBatteryConfigSubscription?.Dispose();

            _quarterlyInfos.Clear();

            // Not _milpService: it is a DI-owned singleton, and StopAsync already disposed the
            // scope it was resolved from.
            _scope.Dispose();

            base.Dispose();
            _isDisposed = true;
        }
    }
}