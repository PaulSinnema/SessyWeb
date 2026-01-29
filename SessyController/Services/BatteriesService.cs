using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;
using static SessyData.Model.SessyWebControl;

namespace SessyController.Services
{
    /// <summary>
    /// This service maintains all batteries in the system.
    /// </summary>
    public partial class BatteriesService : BackgroundHeartbeatService
    {
        private IServiceScope _scope { get; set; }
        private SessyService? _sessyService { get; set; }
        private P1MeterService? _p1MeterService { get; set; }
        private DayAheadMarketService? _dayAheadMarketService { get; set; }
        private SolarInverterManager? _solarInverterManager { get; set; }
        private SolarService? _solarService { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        private ChargingModes _chargingModes;

        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }

        private IDisposable? _sessyBatteryConfigSubscription { get; set; }

        private SettingsConfig _settingsConfig { get; set; }
        private IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor { get; set; }

        private IDisposable? _settingsConfigSubscription { get; set; }

        private SessyBatteryConfig _sessyBatteryConfig { get; set; }
        private BatteryContainer? _batteryContainer { get; set; }
        private TimeZoneService? _timeZoneService { get; set; }
        private PowerEstimatesService _powerEstimatesService { get; set; }
        private WeatherService _weatherService { get; set; }

        private FinancialResultsService _financialResultsService { get; set; }

        private SessyWebControlDataService _sessyWebControlDataService { get; set; }
        private PerformanceDataService _performanceDataService { get; set; }

        private ConsumptionDataService _consumptionDataService { get; set; }
        private EnergyHistoryDataService _energyHistoryDataService { get; set; }

        private ConsumptionMonitorService _consumptionMonitorService { get; set; }

        private VirtualBatteryService _virtualBatteryService { get; set; }

        private LoggingService<BatteriesService> _logger { get; set; }

        private static List<QuarterlyInfo>? _quarterlyInfos { get; set; } = new List<QuarterlyInfo>();

        public bool IsManualOverride => _settingsConfig.ManualOverride;

        public bool WeAreInControl { get; private set; } = true;

        public BatteriesService(LoggingService<BatteriesService> logger,
                                ChargingModes chargingModes,
                                IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
                                IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;

            _logger.LogInformation("BatteriesService starting");

            _serviceScopeFactory = serviceScopeFactory;

            _logger.LogInformation("BatteriesService checking settings");

            _chargingModes = chargingModes;
            _settingsConfigMonitor = settingsConfigMonitor;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;

            _settingsConfigSubscription = _settingsConfigMonitor.OnChange(settings =>
            {
                _settingsConfig = settings;
                _lastSessionCanceled = null;
            });

            _sessyBatteryConfigSubscription = _sessyBatteryConfigMonitor.OnChange((SessyBatteryConfig settings) =>
            {
                _sessyBatteryConfig = settings;
            });

            _settingsConfig = settingsConfigMonitor.CurrentValue;
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue;

            if (_settingsConfig == null) throw new InvalidOperationException("ManagementSettings missing");
            if (_sessyBatteryConfig == null) throw new InvalidOperationException("Sessy:Batteries missing");

            _scope = _serviceScopeFactory.CreateScope();

            _sessyService = _scope.ServiceProvider.GetRequiredService<SessyService>();
            _p1MeterService = _scope.ServiceProvider.GetRequiredService<P1MeterService>();
            _dayAheadMarketService = _scope.ServiceProvider.GetRequiredService<DayAheadMarketService>();
            _solarInverterManager = _scope.ServiceProvider.GetRequiredService<SolarInverterManager>();
            _solarService = _scope.ServiceProvider.GetRequiredService<SolarService>();
            _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _powerEstimatesService = _scope.ServiceProvider.GetRequiredService<PowerEstimatesService>();
            _weatherService = _scope.ServiceProvider.GetRequiredService<WeatherService>();
            _financialResultsService = _scope.ServiceProvider.GetRequiredService<FinancialResultsService>();
            _sessyWebControlDataService = _scope.ServiceProvider.GetRequiredService<SessyWebControlDataService>();
            _performanceDataService = _scope.ServiceProvider.GetRequiredService<PerformanceDataService>();
            _consumptionDataService = _scope.ServiceProvider.GetRequiredService<ConsumptionDataService>();
            _energyHistoryDataService = _scope.ServiceProvider.GetRequiredService<EnergyHistoryDataService>();
            _consumptionMonitorService = _scope.ServiceProvider.GetRequiredService<ConsumptionMonitorService>();
            _virtualBatteryService = _scope.ServiceProvider.GetRequiredService<VirtualBatteryService>();
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _scope.Dispose();

            return base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// Executes the background service, fetching prices periodically.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("Batteries service started ...");

            // Loop to fetch prices every hour
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    GC.Collect();

                    await HeartBeatAsync();

                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"An error occurred while managing batteries.{ex.ToDetailedString()}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancelationToken);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay, keep processing {ex.ToDetailedString()}");
                }
            }

            _logger.LogWarning("BatteriesService stopped.");
        }

        private SemaphoreSlim HourlyInfoSemaphore = new SemaphoreSlim(1);

        private Sessions? _sessions { get; set; } = null;

        /// <summary>
        /// This routine is called periodically as a background task.
        /// </summary>
        public async Task Process(CancellationToken cancellationToken)
        {
            if (_dayAheadMarketService != null && _dayAheadMarketService.IsInitialized())
            {
                // Prevent race conditions.
                await HourlyInfoSemaphore.WaitAsync().ConfigureAwait(false);

                try
                {
                    await DetermineChargingQuarters().ConfigureAwait(false);

                    if (_sessions != null)
                    {
                        await _consumptionMonitorService.EstimateConsumptionInWattsPerQuarter(_quarterlyInfos!).ConfigureAwait(false);

                        await _solarService.GetExpectedSolarPower(_quarterlyInfos!).ConfigureAwait(false);

                        await EvaluateSessions().ConfigureAwait(false);

                        QuarterlyInfo? currentHourlyInfo = _sessions.GetCurrentQuarterlyInfo();

                        if (currentHourlyInfo != null)
                        {
                            Session? currentSession = _sessions.FindSession(currentHourlyInfo);

                            var changed = await EpandCurrentSessionIfNeeded(currentSession!).ConfigureAwait(false);

                            if (changed)
                            {
                                await EvaluateSessions().ConfigureAwait(false);
                            }

                            await StorePerformance(currentHourlyInfo);

                            if (await WeControlTheBatteries().ConfigureAwait(false))
                            {
                                var batteryStates = await GetBatteryStates(currentHourlyInfo);

                                await HandleChargingAndDischarging(batteryStates, currentHourlyInfo).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, $"Unhandled exception in Process: {ex.ToDetailedString()}");
                    // Keep the loop running, just report in the log what went wrong.
                }
                finally
                {
                    HourlyInfoSemaphore.Release();

                    if (DataChanged != null)
                    {
                        await DataChanged?.Invoke();
                    }
                }
            }
        }

        private async Task<bool> EpandCurrentSessionIfNeeded(Session currentSession)
        {
            var changed = false;
            var failsafe = 10;

            if (currentSession != null)
            {
                switch (currentSession.Mode)
                {
                    case Modes.Charging:
                        while (currentSession.Last.ChargeLeft < currentSession.Last.ChargeNeeded && failsafe-- > 0)
                        {
                            var expanded = await ExpandSession(currentSession);

                            if (!expanded)
                                break;

                            changed = true;
                        }

                        break;

                    case Modes.Discharging:
                        while (currentSession.Last.ChargeLeft > currentSession.Last.ChargeNeeded && failsafe-- > 0)
                        {
                            var expanded = await ExpandSession(currentSession);

                            if (!expanded)
                                break;

                            changed = true;
                        }

                        break;

                    default:
                        break;
                }
            }

            return changed;
        }

        private async Task<bool> ExpandSession(Session currentSession)
        {
            var index = _quarterlyInfos.IndexOf(currentSession.Last!) + 1;

            if (index < _quarterlyInfos.Count)
            {
                currentSession.AddQuarterlyInfo(_quarterlyInfos[index++]);

                await RecalculateChargeLeftAndNeeded();

                return true;
            }

            return false;
        }

        private async Task StorePerformance(QuarterlyInfo currentQuarterlyInfo)
        {
            if (!await _performanceDataService.Exists(async (set) =>
            {
                var result = set.Any(pd => pd.Time == currentQuarterlyInfo.Time);
                return await Task.FromResult(result).ConfigureAwait(false);
            }).ConfigureAwait(false))
            {
                var time = currentQuarterlyInfo.Time;

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
                        ChargeLeft = await _batteryContainer.GetStateOfChargeInWatts(),
                        ChargeNeeded = currentQuarterlyInfo.ChargeNeeded,
                        Charging = currentQuarterlyInfo.Charging,
                        Discharging = currentQuarterlyInfo.Discharging,
                        ZeroNetHome = currentQuarterlyInfo.ZeroNetHome,
                        Disabled = currentQuarterlyInfo.Disabled,
                        SolarPowerPerQuarterHour = currentQuarterlyInfo.SolarPowerPerQuarterHour,
                        SmoothedSolarPower = currentQuarterlyInfo.SmoothedSolarPower,
                        SolarGlobalRadiation = currentQuarterlyInfo.SolarGlobalRadiation,
                        ChargeLeftPercentage = currentQuarterlyInfo.ChargeLeftPercentage,
                        DisplayState = currentQuarterlyInfo.GetDisplayMode(),
                        VisualizeInChart = currentQuarterlyInfo.VisualizeInChart(),
                    }
                };

                await _performanceDataService.Add(performanceData).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Checks who has control. If control changed since the last store of data a new record is stored.
        /// </summary>
        private async Task<bool> WeControlTheBatteries()
        {
            var supplierInControl = await SupplierIsControllingTheBatteries();
            var chargedInControl = _settingsConfig.ChargedInControl;

            WeAreInControl = !(supplierInControl || chargedInControl);

            SessyWebControlStatus status = SessyWebControlStatus.SessyWeb;

            if (!WeAreInControl)
            {
                if (_settingsConfig.ChargedInControl)
                {
                    status = SessyWebControlStatus.Charged;
                }

                // Supplier overrules all
                if (supplierInControl)
                {
                    status = SessyWebControlStatus.Provider;
                }
            }

            var last = await _sessyWebControlDataService.Get(async (set) =>
            {
                var result = set.OrderByDescending(sc => sc.Time)
                        .FirstOrDefault();

                return await Task.FromResult(result);
            });

            if (last == null || last.Status != status)
            {
                await StoreStatus(status);
            }

            return WeAreInControl;
        }

        /// <summary>
        /// Store a new control record to the database.
        /// </summary>
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

            await _sessyWebControlDataService.Add(controlList);
        }

        /// <summary>
        /// Checks whether the supplier has control over the batteries.
        /// The StrategyOverridden boolean is true when the supplier is taking control
        /// and false when the supplier doesn't need control anymore.
        /// </summary>
        private async Task<bool> SupplierIsControllingTheBatteries()
        {
            foreach (var battery in _batteryContainer.Batteries)
            {
                var currentPowerStrategy = await battery.GetPowerStatus();

                if (currentPowerStrategy.Sessy.StrategyOverridden)
                {
                    // Supplier is controlling the batteries.
                    return true;
                }
            }

            // We are in control
            return false;
        }

        /// <summary>
        /// Handle (dis)charging manual or automatic.
        /// </summary>
        private async Task HandleChargingAndDischarging(BatteryStates batteryStates, QuarterlyInfo currentHourlyInfo)
        {
            if (_dayAheadMarketService.IsInitialized())
            {
                if (_settingsConfig.ManualOverride == false)
                {
                    await HandleAutomaticCharging(_sessions!, currentHourlyInfo, batteryStates);
                }
                else
                {
                    await HandleManualCharging(currentHourlyInfo);
                }
            }
            else
            {
                _logger.LogInformation("No prices available from ENTSO-E, switching to manual charging");

                await HandleManualCharging(currentHourlyInfo);
            }
        }

        /// <summary>
        /// Returns the fetched prices and analyzes them.
        /// </summary>
        public List<QuarterlyInfo>? GetQuarterlyInfos()
        {
            HourlyInfoSemaphore.Wait();

            try
            {
                return _quarterlyInfos;
            }
            finally
            {
                HourlyInfoSemaphore.Release();
            }
        }

        public Sessions GetSessions()
        {
            return _sessions!;
        }

        private async Task HandleAutomaticCharging(Sessions sessions, QuarterlyInfo currentHourlyInfo, BatteryStates batteryStates)
        {
            var currentSession = sessions.GetSession(currentHourlyInfo);

            await CancelSessionIfStateRequiresIt(sessions, currentHourlyInfo, batteryStates);

            switch (currentHourlyInfo.Mode)
            {
                case Modes.Charging:
                    {
                        var chargingPower = currentSession.GetChargingPowerInWattsPerHour();
#if !DEBUG
                        await _batteryContainer.StartCharging(chargingPower);
#endif
                        break;
                    }

                case Modes.Discharging:
                    {
                        var chargingPower = currentSession.GetChargingPowerInWattsPerHour();
#if !DEBUG
                        await _batteryContainer.StartDisharging(chargingPower);
#endif
                        break;
                    }

                case Modes.ZeroNetHome:
                    {
#if !DEBUG
                        await _batteryContainer.StartNetZeroHome();
#endif
                        break;
                    }

                case Modes.Disabled:
                case Modes.Unknown:
                default:
                    {
#if !DEBUG
                        await _batteryContainer.StopAll();
#endif
                        break;
                    }

            }
        }

        private async Task HandleManualCharging(QuarterlyInfo currentQuarterlyInfo)
        {
#if !DEBUG
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHours != null && _settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartCharging(_batteryContainer.GetChargingCapacityInWattsPerHour());
            else if (_settingsConfig.ManualDischargingHours != null && _settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartDisharging(_batteryContainer.GetDischargingCapacityInWattsPerHour());
            else if (_settingsConfig.ManualNetZeroHomeHours != null && _settingsConfig.ManualNetZeroHomeHours.Contains(localTime.Hour))
                await _batteryContainer.StartNetZeroHome();
            else
                await _batteryContainer.StopAll();
#else
            await Task.Delay(1); // Prevent warning in debug mode
#endif
        }

        private class SessionInfo
        {
            public SessionInfo(Session? session)
            {
                FirstDate = session.FirstDateTime;
                LastDate = session.LastDateTime;
            }

            public DateTime FirstDate { get; private set; }
            public DateTime LastDate { get; private set; }

            public bool IsInsideTimeFrame(QuarterlyInfo quarterlyInfo)
            {
                return FirstDate <= quarterlyInfo.Time && LastDate >= quarterlyInfo.Time;
            }
        }

        private SessionInfo? _lastSessionCanceled { get; set; } = null;

        /// <summary>
        /// If batteries are full (enough) stop charging this session
        /// If batteries are empty stop discharging this session
        /// </summary>
        private async Task CancelSessionIfStateRequiresIt(Sessions sessions, QuarterlyInfo currentHourlyInfo, BatteryStates batteryStates)
        {
            var session = sessions.GetSession(currentHourlyInfo);

            if (session != null)
            {
                if (_lastSessionCanceled != null)
                {
                    if (_lastSessionCanceled.IsInsideTimeFrame(currentHourlyInfo))
                    {
                        _logger.LogInformation($"Session was previously canceled {session}");

                        StopSession(session);

                        return;
                    }
                }

                if (session.Mode == Modes.Charging)
                {
                    if (batteryStates.BatteriesAreFull || batteryStates.ChargeAtMaximum)
                    {
                        StopSession(session);

                        _logger.LogWarning($"Warning: Charging session stopped because batteries are full (enough). {session}, batteries are full: {batteryStates.BatteriesAreFull}, charge at maximum: {batteryStates.ChargeAtMaximum}");
                    }
                }
                else if (session.Mode == Modes.Discharging)
                {
                    bool batteriesAreEmpty = await AreAllBatteriesEmpty();
                    bool chargeAtMinimum = await IsMinChargeNeededReached(currentHourlyInfo);

                    if (batteriesAreEmpty || chargeAtMinimum)
                    {
                        StopSession(session);

                        _logger.LogWarning($"Warning: Discharging session stopped. Min. charge reached or batteries are empty. {session}, batteries are empty: {batteriesAreEmpty}, charge at minimum: {chargeAtMinimum}");
                    }
                }
            }
        }

        public class BatteryStates
        {
            public bool BatteriesAreFull { get; set; }
            public bool ChargeAtMaximum { get; set; }
        }

        /// <summary>
        /// Gets states for the battery. BatteriesAreFull and ChargeAtMaximum.
        /// </summary>
        private async Task<BatteryStates> GetBatteryStates(QuarterlyInfo currentHourlyInfo)
        {
            BatteryStates batteryStates = new();

            batteryStates.BatteriesAreFull = await AreAllBatteriesFull();
            batteryStates.ChargeAtMaximum = await IsMaxChargeNeededReached(currentHourlyInfo);

            return batteryStates;
        }

        /// <summary>
        /// Check whether the current hourly info is in a discharging session and return
        /// true if so and the calculated min charge needed is reached.
        /// </summary>
        private async Task<bool> IsMinChargeNeededReached(QuarterlyInfo currentHourlyInfo)
        {
            var currentChargeState = await _batteryContainer.GetStateOfChargeInWatts();

            return (currentHourlyInfo.Discharging && currentChargeState <= currentHourlyInfo.ChargeNeeded);
        }

        /// <summary>
        /// Check whether the current hourly info is in a charging session and return
        /// true if so and the calculated max charge needed is reached.
        /// </summary>
        private async Task<bool> IsMaxChargeNeededReached(QuarterlyInfo currentHourlyInfo)
        {
            var currentChargeState = await _batteryContainer.GetStateOfChargeInWatts();

            return (currentHourlyInfo.Charging && currentChargeState >= currentHourlyInfo.ChargeNeeded);
        }

        /// <summary>
        /// Cancel (dis)charging for current session.
        /// </summary>
        private void StopSession(Session? session)
        {
            _lastSessionCanceled = new SessionInfo(session);

            _sessions.RemoveSession(session);
        }

        /// <summary>
        /// Returns true if all batteries have state SYSTEM_STATE_BATTERY_FULL
        /// </summary>
        private async Task<bool> AreAllBatteriesFull()
        {
            var batteriesAreFull = true;

            foreach (var battery in _batteryContainer.Batteries)
            {
                var systemState = await battery.GetPowerStatus();

                if (systemState.Sessy.SystemState != Sessy.SystemStates.SYSTEM_STATE_BATTERY_FULL)
                {
                    batteriesAreFull = false;
                    break;
                }
            }

            return batteriesAreFull;
        }

        /// <summary>
        /// Returns true if all batteries have state SYSTEM_STATE_BATTERY_EMPTY
        /// </summary>
        private async Task<bool> AreAllBatteriesEmpty()
        {
            var batteriesAreEmpty = true;

            foreach (var battery in _batteryContainer.Batteries)
            {
                var systemState = await battery.GetPowerStatus();

                if (systemState.Sessy.SystemState != Sessy.SystemStates.SYSTEM_STATE_BATTERY_EMPTY)
                {
                    batteriesAreEmpty = false;
                    break;
                }
            }

            return batteriesAreEmpty;
        }

        /// <summary>
        /// Determine when to charge the batteries.
        /// </summary>
        public async Task DetermineChargingQuarters()
        {
            DateTime localTime = _timeZoneService.Now;

            if (await FetchPricesFromENTSO_E(localTime))
            {
                await GetChargingHours();
            }
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        private async Task<bool> FetchPricesFromENTSO_E(DateTime localTime)
        {
            // Get the available hourly prices.
            var prices = await _dayAheadMarketService.GetPrices();

            _quarterlyInfos = prices
                .OrderBy(hp => hp.Time)
                .ToList();

            QuarterlyInfo.AddSmoothedPrices(_quarterlyInfos, 6);

            return _quarterlyInfos != null && _quarterlyInfos.Count > 0;
        }

        private DateTime? lastSessionCreationDate { get; set; } = null;

        public delegate Task DataChangedDelegate();

        public event DataChangedDelegate? DataChanged;

        /// <summary>
        /// In this routine it is determined when to charge the batteries.
        /// </summary>
        private async Task GetChargingHours()
        {
            try
            {
                _quarterlyInfos = _quarterlyInfos!
                    .OrderBy(hp => hp.Time)
                    .ToList();

                CreateSessions();

                if (_sessions != null)
                {
                    await CalculateDeltaLowestPrice().ConfigureAwait(false);

                    MergeNeighbouringSessions();

                    await CheckSessions();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unhandled exception in GetChargingHours{ex.ToDetailedString()}");
            }
        }

        /// <summary>
        /// Merge sesseion that have no info objects between them.
        /// </summary>
        private void MergeNeighbouringSessions()
        {
            Session? previousSession = null;

            foreach (var nextSession in _sessions.SessionList.OrderBy(se => se.FirstDateTime).ToList())
            {
                if (previousSession != null)
                {
                    if (previousSession.Mode == nextSession.Mode &&
                        previousSession.Last.Time.AddMinutes(15) == nextSession.FirstDateTime)
                    {
                        previousSession.Merge(nextSession);
                        _sessions.RemoveSession(nextSession, false);
                        continue;
                    }
                }

                previousSession = nextSession;
            }
        }

        /// <summary>
        /// This method is voor debugging purposes only. It checks the content of hourlyInfos
        /// and sessions.
        /// </summary>
        private async Task CheckSessions()
        {
            foreach (var session in _sessions.SessionList)
            {
                if (session.GetQuarterlyInfoList().Count() == 0)
                    throw new InvalidOperationException($"Session without HourlyInfos");
            }

            foreach (var session in _sessions.SessionList)
            {
                switch (session.Mode)
                {
                    case Modes.Charging:
                        foreach (var hi in session.GetQuarterlyInfoList())
                        {
                            var mode = hi.Mode;
                            if (mode != Modes.Charging)
                                throw new InvalidOperationException($"Charging session has hourlyinfo objects without charging mode {session}");
                        }

                        break;

                    case Modes.Discharging:
                        foreach (var hi in session.GetQuarterlyInfoList())
                        {
                            var mode = hi.Mode;
                            if (mode != Modes.Discharging)
                                throw new InvalidOperationException($"Discharging session has hourlyinfo objects without discharging mode {session}");
                        }

                        break;

                    case Modes.Unknown:
                    case Modes.ZeroNetHome:
                    default:
                        throw new InvalidOperationException($"Session has wrong mode {session}");
                }
            }

            foreach (var quarterlyInfo in _quarterlyInfos)
            {
                switch (quarterlyInfo.Mode)
                {
                    case Modes.Charging:
                        {
                            if (!_sessions.InAnySession(quarterlyInfo))
                                throw new InvalidOperationException($"Info not in a session {quarterlyInfo}");

                            Session session = CheckSession(_sessions, quarterlyInfo);

                            if (session.Mode != Modes.Charging)
                                throw new InvalidOperationException($"Charging info in wrong session {quarterlyInfo}");

                            break;
                        }

                    case Modes.Discharging:
                        {
                            if (!_sessions.InAnySession(quarterlyInfo))
                                throw new InvalidOperationException($"Info not in a session {quarterlyInfo}");

                            Session session = CheckSession(_sessions, quarterlyInfo);

                            if (session.Mode != Modes.Discharging)
                                throw new InvalidOperationException($"Discharging info in wrong session {quarterlyInfo}");

                            break;
                        }

                    case Modes.ZeroNetHome:
                    default:
                        if (_sessions.InAnySession(quarterlyInfo))
                        {
                            throw new InvalidOperationException($"Hourlyinfo should not be in any session {quarterlyInfo}");
                        }

                        break;
                }
            }
        }

        private Session CheckSession(Sessions sessions, QuarterlyInfo quarterlyInfo)
        {
            var session = sessions.FindSession(quarterlyInfo);

            if (session.GetQuarterlyInfoList().Count < 1)
                throw new InvalidOperationException($"Empty charging session {session}");

            return session;
        }

        private async Task EvaluateSessions()
        {
            do
            {
                await RecalculateChargeLeftAndNeeded();

                await RemoveExtraChargingSessions();

                await CheckSessions();

                // await EvaluateChargingHoursAndProfitability();
            }
            while (await ShrinkSessions());

            await CheckForFutureChargingSessions();

            await RecalculateChargeLeftAndNeeded();
        }

        /// <summary>
        /// If there are no charging sessions in the future at least
        /// create a charging session for the charge needed to get to the
        /// next day as cheap as possible.
        /// </summary>
        private async Task CheckForFutureChargingSessions()
        {
            if (ThereAreNoFutureSessions())
            {
                var quarterlyInfo = await FindCheapestQuarterlyInfo();

                if (quarterlyInfo != null)
                {
                    var session = _sessions.AddNewSession(Modes.Charging, quarterlyInfo);

                    if (session != null)
                    {
                        _sessions.CompleteSession(session);

                        await RecalculateChargeLeftAndNeeded();
                    }
                }

                await ShrinkSessions();
            }
        }

        private bool ThereAreNoFutureSessions()
        {
            var now = _timeZoneService.Now;
            var list = _sessions.SessionList.Where(se => se.LastDateTime > now);

            var result = !list.Any(se => se.Mode == Modes.Charging);

            return result;
        }

        public async Task<QuarterlyInfo?> FindCheapestQuarterlyInfo()
        {
            var now = _timeZoneService.Now;

            QuarterlyInfo? best = null;

            foreach (var qi in _quarterlyInfos!.Where(q => q.Time >= now).OrderBy(q => q.BuyingPrice))
            {
                var mode = qi.Mode;
                if (mode is Modes.Charging or Modes.Discharging)
                    continue;

                best = qi;
                break; // eerste (dus goedkoopste) die voldoet
            }

            return best;
        }


        private async Task EvaluateChargingHoursAndProfitability()
        {
            do
            {
                await RecalculateChargeLeftAndNeeded();

                _sessions.CalculateProfits(_timeZoneService!);
            }
            while (_sessions.RemoveMoreExpensiveChargingSessions());

            await RecalculateChargeLeftAndNeeded();
        }

        public async Task<double> CalculateAveragePriceOfChargeInBatteries()
        {
            double chargingCapacity = _sessyBatteryConfig.TotalChargingCapacity / 4.0; // Per quarter hour
            double dischargingCapacity = _sessyBatteryConfig.TotalDischargingCapacity / 4.0; // Per quarter hour
            var to = _timeZoneService.Now;
            var from = _quarterlyInfos!.Min(qi => qi.Time);

            return await _performanceDataService.CalculateAveragePriceOfChargeInBatteries(chargingCapacity, dischargingCapacity, from, to);
        }

        /// <summary>
        /// Calculate the estimated charge per hour starting from the current hour.
        /// </summary>
        public async Task CalculateChargeLeft()
        {
            double charge = 0.0;
            double totalCapacity = _batteryContainer.GetTotalCapacity();
            double chargingCapacity = _sessyBatteryConfig.TotalChargingCapacity / 4.0; // Per quarter hour
            double dischargingCapacity = _sessyBatteryConfig.TotalDischargingCapacity / 4.0; // Per quarter hour
            var now = _timeZoneService.Now;
            var localTimeHour = now.Date.AddHours(now.Hour);
            charge = await _batteryContainer.GetStateOfChargeInWatts();

            var hourlyInfoList = _quarterlyInfos!
                .OrderBy(hp => hp.Time)
                .ToList();

            hourlyInfoList.ForEach(hi => hi.SetChargeLeft(charge));

            var list = hourlyInfoList.Where(hi => hi.Time >= now.DateFloorQuarter());

            foreach (var quarterlyInfo in list)
            {
                switch (quarterlyInfo.Mode)
                {
                    case Modes.Charging:
                        {
                            if (charge < quarterlyInfo.ChargeNeeded)
                            {
                                var toCharge = quarterlyInfo.ChargeNeeded - charge;

                                charge += Math.Min(toCharge, chargingCapacity);
                            }

                            break;
                        }

                    case Modes.Discharging:
                        {
                            if (charge > quarterlyInfo.ChargeNeeded)
                            {
                                var toDischarge = charge - quarterlyInfo.ChargeNeeded;
                                charge -= Math.Min(toDischarge, dischargingCapacity);
                            }

                            break;
                        }

                    case Modes.ZeroNetHome:
                        {
                            charge -= quarterlyInfo.EstimatedConsumptionPerQuarterInWatts;
                            charge += quarterlyInfo.SolarPowerPerQuarterInWatts;

                            break;
                        }

                    case Modes.Disabled:
                        break;

                    default:
                        throw new InvalidOperationException($"Invalid mode for quarterlyInfo: {quarterlyInfo}");
                }

                if (charge < 0) charge = 0.0;
                if (charge > totalCapacity) charge = totalCapacity;

                quarterlyInfo.SetChargeLeft(charge);
            }
        }

        /// <summary>
        /// In this fase creating all sessions has finished. Now it's time to evaluate which
        /// ones to keep. The following sessions are filtered out.
        /// - Charging sessions larger than the max charging hours
        /// - Discharging sessions that are not profitable.
        /// </summary>
        private async Task RemoveExtraChargingSessions()
        {
            bool changed;

            do
            {
                changed = await CheckProfitability();

            } while (changed);

            await RecalculateChargeLeftAndNeeded();
        }

        /// <summary>
        /// Check if discharging sessions are profitable.
        /// </summary>
        private async Task<bool> CheckProfitabilityNew()
        {
            bool changed = false;
            var now = _timeZoneService.Now;

            Session? lastSession = null;

            foreach (var session in _sessions.SessionList.OrderBy(se => se.FirstDateTime).ToList())
            {
                if (lastSession != null)
                {
                    if (session.LastDateTime > now && lastSession.Mode == Modes.Charging && session.Mode == Modes.Discharging)
                    {
                        var chargingCost = await _virtualBatteryService.CalculateLoadCostForSession(lastSession);
                        var dischargingCost = await _virtualBatteryService.CalculateLoadCostForSession(session);
                        var differnceCost = dischargingCost + chargingCost;

                        if (differnceCost < _settingsConfig.CycleCost)
                        {
                            _sessions.RemoveSession(session);
                            changed = true;
                        }
                    }
                }

                lastSession = session;
            }

            return changed;
        }

        /// <summary>
        /// Check if discharging sessions are profitable.
        /// </summary>
        private async Task<bool> CheckProfitability()
        {
            bool changed = false;
            var averagePriceInBattery = await CalculateAveragePriceOfChargeInBatteries();

            Session? previousSession = null;

            foreach (var nextSession in _sessions.SessionList.OrderBy(se => se.FirstDateTime))
            {
                if (previousSession != null)
                {
                    if (previousSession.Mode == Modes.Charging && nextSession.Mode == Modes.Discharging)
                    {
                        using var chargeEnumerator = previousSession.GetQuarterlyInfoList().OrderBy(hi => hi.Price).ToList().GetEnumerator();
                        using var dischargeEnumerator = nextSession.GetQuarterlyInfoList().OrderByDescending(hi => hi.Price).ToList().GetEnumerator();

                        var hasCharging = chargeEnumerator.MoveNext();
                        var hasDischarging = dischargeEnumerator.MoveNext();

                        while (hasCharging && hasDischarging)
                        {
                            if (averagePriceInBattery + _settingsConfig.CycleCost > dischargeEnumerator.Current.Price)
                            {
                                nextSession.RemoveQuarterlyInfo(dischargeEnumerator.Current);

                                hasDischarging = dischargeEnumerator.MoveNext();

                                changed = true;
                            }
                            else
                            {
                                hasCharging = chargeEnumerator.MoveNext();
                                hasDischarging = dischargeEnumerator.MoveNext();
                            }
                        }
                    }
                }

                previousSession = nextSession;
            }

            changed |= RemoveEmptySessions();

            await RecalculateChargeLeftAndNeeded();

            return await Task.FromResult(changed);
        }

        /// <summary>
        /// Remove sessions without (dis)charging hours.
        /// </summary>
        private bool RemoveEmptySessions()
        {
            var changed = false;

            foreach (var session in _sessions.SessionList.ToList())
            {
                if (session.GetQuarterlyInfoList().Count() == 0)
                {
                    _sessions.RemoveSession(session);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Shrink sessions if the hours needed to charge to 100% is less than calculated.
        /// </summary>
        private async Task<bool> ShrinkSessions()
        {
            var changed = false;

            foreach (var session in _sessions.SessionList)
            {
                var maxQuarters = session.GetQuartersForMode();

                if (session.RemoveAllAfter(maxQuarters))
                {
                    changed = true;
                }
            }

            changed |= RemoveEmptySessions();

            await RecalculateChargeLeftAndNeeded();

            return changed;
        }

        private async Task RecalculateChargeLeftAndNeeded()
        {
            await CalculateChargeNeeded();

            await CalculateChargeLeft();
        }

        /// <summary>
        /// This routine detects charge session that follow each other and determines how much
        /// charge is needed for the session.
        /// </summary>
        private async Task CalculateChargeNeeded()
        {
            _sessions.SetEstimateChargeNeededUntilNextSession();
        }

        private async Task CalculateDeltaLowestPrice()
        {
            await _sessions.CalculateDeltaLowestPrice().ConfigureAwait(false);
        }

        // Helpers: signal smoothing + prominence-based peak/trough detection
        static class SignalExtrema
        {
            // Compute median sampling interval in minutes (robust to outliers)
            public static int DetectSamplingMinutes(IReadOnlyList<DateTime> times)
            {
                if (times.Count < 2) return 15;
                var deltas = new List<double>(times.Count - 1);
                for (int i = 1; i < times.Count; i++)
                    deltas.Add((times[i] - times[i - 1]).TotalMinutes);
                deltas.Sort();
                double median = deltas[deltas.Count / 2];
                return (int)Math.Max(1, Math.Round(median));
            }

            // Simple centered moving average (odd window preferred). Edges use partial windows.
            public static double[] MovingAverage(IList<double> values, int window)
            {
                if (values.Count == 0 || window <= 1) return values.ToArray();
                int half = window / 2;
                var outv = new double[values.Count];

                for (int i = 0; i < values.Count; i++)
                {
                    int s = Math.Max(0, i - half);
                    int e = Math.Min(values.Count - 1, i + half);
                    double sum = 0; int n = 0;
                    for (int j = s; j <= e; j++) { sum += values[j]; n++; }
                    outv[i] = sum / n;
                }
                return outv;
            }

            // Robust scale estimate using IQR (Q75 - Q25)
            public static (double mean, double iqr, double stdLike) Stats(IList<double> values)
            {
                if (values.Count == 0) return (0, 0, 0);
                var sorted = values.OrderBy(v => v).ToArray();
                double q25 = Quantile(sorted, 0.25);
                double q75 = Quantile(sorted, 0.75);
                double iqr = q75 - q25;

                double mean = values.Average();
                // Approximate robust sigma from IQR for normal-ish data
                double stdLike = iqr / 1.349; // ≈ sigma
                return (mean, iqr, stdLike);

                static double Quantile(double[] arr, double p)
                {
                    if (arr.Length == 1) return arr[0];
                    double pos = p * (arr.Length - 1);
                    int i = (int)Math.Floor(pos);
                    double frac = pos - i;
                    if (i >= arr.Length - 1) return arr[^1];
                    return arr[i] * (1 - frac) + arr[i + 1] * frac;
                }
            }

            // Find local maxima indices with prominence and minDistance
            public static List<int> FindPeaks(
                IList<double> x,
                double minProminence,
                int minDistance)
            {
                var candidates = new List<int>();
                for (int i = 1; i < x.Count - 1; i++)
                {
                    if (x[i] > x[i - 1] && x[i] >= x[i + 1]) candidates.Add(i);
                }
                if (candidates.Count == 0) return candidates;

                var accepted = new List<(int idx, double prom)>();

                foreach (var i in candidates)
                {
                    double peak = x[i];

                    // Walk left to first higher point; track lowest saddle
                    double leftMin = peak;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (x[j] > peak) break;
                        leftMin = Math.Min(leftMin, x[j]);
                    }
                    // Walk right to first higher point; track lowest saddle
                    double rightMin = peak;
                    for (int j = i + 1; j < x.Count; j++)
                    {
                        if (x[j] > peak) break;
                        rightMin = Math.Min(rightMin, x[j]);
                    }
                    double saddle = Math.Max(leftMin, rightMin);
                    double prom = peak - saddle;

                    if (prom >= minProminence) accepted.Add((i, prom));
                }

                // Enforce minDistance by keeping the most prominent first
                var result = new List<int>();
                foreach (var (idx, _) in accepted.OrderByDescending(p => p.prom))
                {
                    if (result.All(r => Math.Abs(r - idx) >= minDistance))
                        result.Add(idx);
                }
                result.Sort();
                return result;
            }

            // Find local minima by inverting the signal and reusing peak logic
            public static List<int> FindTroughs(
                IList<double> x,
                double minProminence,
                int minDistance)
            {
                var inv = x.Select(v => -v).ToArray();
                return FindPeaks(inv, minProminence, minDistance);
            }
        }

        // ====== Your method rewritten ======
        private void CreateSessions()
        {
            _sessions?.Dispose();
            _sessions = null;

            if (_quarterlyInfos == null || _quarterlyInfos.Count == 0)
            {
                _logger.LogWarning("QuarterlyInfos is empty!!");
                return;
            }

            var loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

            // Sort consistently
            var list = _quarterlyInfos.OrderBy(hi => hi.Time).ToList();

            // Prepare arrays
            var times = list.Select(x => x.Time).ToList();
            var buy = list.Select(x => x.BuyingPrice).ToList();
            var sell = list.Select(x => x.SellingPrice).ToList();

            // Detect sampling cadence (min)
            int stepMin = SignalExtrema.DetectSamplingMinutes(times);

            // Choose smoothing window ~ 1 hour (odd number)
            int targetSmoothMinutes = 60; // TODO: make configurable (_settingsConfig?)
            int smoothWindow = Math.Max(3, (int)Math.Round((double)targetSmoothMinutes / stepMin));
            if (smoothWindow % 2 == 0) smoothWindow++; // prefer odd

            // Smooth the series (light smoothing to kill tiny bumps)
            var buySmooth = SignalExtrema.MovingAverage(buy, smoothWindow);
            var sellSmooth = SignalExtrema.MovingAverage(sell, smoothWindow);

            // Robust scale to set default prominence thresholds
            var (_, buyIqr, buySigma) = SignalExtrema.Stats(buySmooth);
            var (_, sellIqr, sellSigma) = SignalExtrema.Stats(sellSmooth);

            // Default: require at least ~0.75 * sigma prominence (tune as needed)
            // For very flat series, fall back to IQR fraction.
            double minPromBuy = Math.Max(0.0, 0.75 * (buySigma > 0 ? buySigma : buyIqr / 1.349));
            double minPromSell = Math.Max(0.0, 0.75 * (sellSigma > 0 ? sellSigma : sellIqr / 1.349));

            // Minimal spacing between major extrema ~ 1 hour
            int minDistance = Math.Max(2, (int)Math.Round(60.0 / stepMin));

            // Detect major troughs in Buying (Charging) and peaks in Selling (Discharging)
            var buyTroughIdx = SignalExtrema.FindTroughs(buySmooth, minPromBuy, minDistance);
            var sellPeakIdx = SignalExtrema.FindPeaks(sellSmooth, minPromSell, minDistance);

            // Create sessions object
            _sessions = new Sessions(_quarterlyInfos,
                                     _settingsConfig,
                                     _sessyBatteryConfig,
                                     _batteryContainer!,
                                     _timeZoneService,
                                     _virtualBatteryService,
                                     _financialResultsService,
                                     _performanceDataService,
                                     _consumptionDataService,
                                     _consumptionMonitorService,
                                     _energyHistoryDataService,
                                     loggerFactory);

            // Seed sessions from detected extrema
            // Safety: still honor InAnySession (your existing logic)
            foreach (var i in buyTroughIdx)
            {
                var q = list[i];
                if (!_sessions.InAnySession(q))
                    _sessions.AddNewSession(Modes.Charging, q);
            }
            foreach (var i in sellPeakIdx)
            {
                var q = list[i];
                if (!_sessions.InAnySession(q))
                    _sessions.AddNewSession(Modes.Discharging, q);
            }

            // Optional: also consider endpoints if they are extreme w.r.t. neighbors (edge handling)
            if (list.Count > 1)
            {
                // Left edge
                if (buySmooth[0] <= buySmooth[1] - minPromBuy)
                {
                    var q0 = list[0];
                    if (!_sessions.InAnySession(q0))
                        _sessions.AddNewSession(Modes.Charging, q0);
                }
                if (sellSmooth[0] >= sellSmooth[1] + minPromSell)
                {
                    var q0 = list[0];
                    if (!_sessions.InAnySession(q0))
                        _sessions.AddNewSession(Modes.Discharging, q0);
                }

                // Right edge
                int last = list.Count - 1;
                if (buySmooth[last] <= buySmooth[last - 1] - minPromBuy)
                {
                    var qn = list[last];
                    if (!_sessions.InAnySession(qn))
                        _sessions.AddNewSession(Modes.Charging, qn);
                }
                if (sellSmooth[last] >= sellSmooth[last - 1] + minPromSell)
                {
                    var qn = list[last];
                    if (!_sessions.InAnySession(qn))
                        _sessions.AddNewSession(Modes.Discharging, qn);
                }
            }

            // Let the profitability pruning & completion logic do its work
            while (_sessions.RemoveLessProfitableSessions()) { /* keep pruning */ }

            _sessions.CompleteAllSessions();
        }


        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _settingsConfigSubscription.Dispose();
                _sessyBatteryConfigSubscription.Dispose();

                _quarterlyInfos.Clear();
                _quarterlyInfos = null;
                _sessyService = null;
                _p1MeterService = null;
                _dayAheadMarketService = null;
                _solarInverterManager = null;
                _solarService = null;
                _batteryContainer = null;
                _timeZoneService = null;

                _scope.Dispose();

                base.Dispose();

                _isDisposed = true;
            }
        }

        public async Task<double> getBatteryPercentage()
        {
            return await _batteryContainer.GetBatterPercentage();
        }

        public async Task<string> GetBatteryMode()
        {
            var quarterlyInfo = _sessions.GetCurrentQuarterlyInfo();

            if(quarterlyInfo != null)
                return  _chargingModes.GetDisplayMode(quarterlyInfo.Mode);

            return "???";
        }

        public QuarterlyInfo? GetNextQuarterlyInfoInSession()
        {
            var now = _timeZoneService.Now;

            return _sessions?.GetNextQuarterlyInfoInSession(now);
        }
    }
}
