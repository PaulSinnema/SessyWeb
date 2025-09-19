using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.Session;
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
        private EnergyHistoryService _energyHistoryDataService { get; set; }

        private ConsumptionMonitorService _consumptionMonitorService { get; set; }

        private LoggingService<BatteriesService> _logger { get; set; }

        private static List<QuarterlyInfo>? quarterlyInfos { get; set; } = new List<QuarterlyInfo>();

        public bool IsManualOverride => _settingsConfig.ManualOverride;

        public bool WeAreInControl { get; private set; } = true;

        public BatteriesService(LoggingService<BatteriesService> logger,
                                IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
                                IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;

            _logger.LogInformation("BatteriesService starting");

            _serviceScopeFactory = serviceScopeFactory;

            _logger.LogInformation("BatteriesService checking settings");

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
            _energyHistoryDataService = _scope.ServiceProvider.GetRequiredService<EnergyHistoryService>();
            _consumptionMonitorService = _scope.ServiceProvider.GetRequiredService<ConsumptionMonitorService>();
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
            if (_dayAheadMarketService != null && _dayAheadMarketService.PricesInitialized)
            {
                // Prevent race conditions.
                await HourlyInfoSemaphore.WaitAsync().ConfigureAwait(false);

                try
                {
                    await DetermineChargingQuarters().ConfigureAwait(false);

                    if (_sessions != null)
                    {
                        await _consumptionMonitorService.EstimateConsumptionInWattsPerQuarter(quarterlyInfos!).ConfigureAwait(false);

                        await _solarService.GetExpectedSolarPower(quarterlyInfos!).ConfigureAwait(false);

                        await EvaluateSessions().ConfigureAwait(false);

                        QuarterlyInfo? currentHourlyInfo = _sessions.GetCurrentQuarterlyInfo();

                        if (currentHourlyInfo != null)
                        {
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
                        EstimatedConsumptionPerQuarterHour = currentQuarterlyInfo.EstimatedConsumptionPerQuarterHour,
                        ChargeLeft = await _batteryContainer.GetStateOfChargeInWatts(),
                        ChargeNeeded = currentQuarterlyInfo.ChargeNeeded,
                        Charging = currentQuarterlyInfo.Charging,
                        Discharging = currentQuarterlyInfo.Discharging,
                        SolarPowerPerQuarterHour = currentQuarterlyInfo.SolarPowerPerQuarterHour,
                        SmoothedSolarPower = currentQuarterlyInfo.SmoothedSolarPower,
                        SolarGlobalRadiation = currentQuarterlyInfo.SolarGlobalRadiation,
                        ChargeLeftPercentage = currentQuarterlyInfo.ChargeLeftPercentage,
                        DisplayState = currentQuarterlyInfo.DisplayState,
                        VisualizeInChart = currentQuarterlyInfo.VisualizeInChart,
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
            WeAreInControl = !await SupplierIsControllingTheBatteries();

            SessyWebControlStatus status = WeAreInControl ? SessyWebControlStatus.SessyWeb : SessyWebControlStatus.Provider;

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
            if (_dayAheadMarketService.PricesAvailable)
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
                return quarterlyInfos;
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
                        var chargingPower = currentSession.GetChargingPowerInWatts(currentHourlyInfo);
#if !DEBUG
                        await _batteryContainer.StartCharging(chargingPower);
#endif
                        break;
                    }

                case Modes.Discharging:
                    {
                        var chargingPower = currentSession.GetChargingPowerInWatts(currentHourlyInfo);
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

        private async Task HandleManualCharging(QuarterlyInfo currentHourlyInfo)
        {
#if !DEBUG
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHours != null && _settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartCharging(_batteryContainer.GetChargingCapacityInWatts());
            else if (_settingsConfig.ManualDischargingHours != null && _settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartDisharging(_batteryContainer.GetDischargingCapacityInWatts());
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
                GetChargingHours();
            }
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        private async Task<bool> FetchPricesFromENTSO_E(DateTime localTime)
        {
            // Get the available hourly prices.
            var prices = await _dayAheadMarketService.GetPrices();

            quarterlyInfos = prices
                .OrderBy(hp => hp.Time)
                .ToList();

            QuarterlyInfo.AddSmoothedPrices(quarterlyInfos, 6);

            return quarterlyInfos != null && quarterlyInfos.Count > 0;
        }

        private DateTime? lastSessionCreationDate { get; set; } = null;

        public delegate Task DataChangedDelegate();

        public event DataChangedDelegate? DataChanged;

        /// <summary>
        /// In this routine it is determined when to charge the batteries.
        /// </summary>
        private void GetChargingHours()
        {
            try
            {
                DateTime now = _timeZoneService.Now;
                DateTime nowHour = now.Date.AddHours(now.Hour);

                quarterlyInfos = quarterlyInfos!
                    .OrderBy(hp => hp.Time)
                    .ToList();

                CreateSessions();

                if (_sessions != null)
                {
                    MergeNeighbouringSessions();

                    CheckSessions();

                    RemoveExtraChargingSessions();

                    CheckSessions();
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
        private void CheckSessions()
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
                        if (!session.GetQuarterlyInfoList().All(hi => hi.Mode == Modes.Charging))
                            throw new InvalidOperationException($"Charging session has hourlyinfo objects without charging mode {session}");

                        break;

                    case Modes.Discharging:
                        if (!session.GetQuarterlyInfoList().All(hi => hi.Mode == Modes.Discharging))
                            throw new InvalidOperationException($"Discharging session has hourlyinfo objects without discharging mode {session}");

                        break;

                    case Modes.Unknown:
                    case Modes.ZeroNetHome:
                    default:
                        throw new InvalidOperationException($"Session has wrong mode {session}");
                }
            }

            foreach (var quarterlyInfo in quarterlyInfos)
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

        private static Session CheckSession(Sessions sessions, QuarterlyInfo quarterlyInfo)
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
                await EvaluateChargingHoursAndProfitability();
            }
            while (ShrinkSessions());
        }

        private async Task EvaluateChargingHoursAndProfitability()
        {
            do
            {
                CalculateChargeNeeded();

                await CalculateChargeLeft();

                _sessions.CalculateProfits(_timeZoneService!);
            }
            while (_sessions.RemoveMoreExpensiveChargingSessions());
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

            var hourlyInfoList = quarterlyInfos!
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
                            charge -= quarterlyInfo.EstimatedConsumptionPerQuarterHour;
                            charge += quarterlyInfo.SolarPowerPerQuarterInWatts;

                            break;
                        }

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
        private void RemoveExtraChargingSessions()
        {
            bool changed1;
            bool changed2;

            do
            {
                changed1 = false;
                changed2 = false;

                changed1 = CheckProfitability();

                changed2 = RemoveEmptySessions();

            } while (changed1 || changed2);
        }

        private bool RemoveDoubleDischargingSessions() // TODO: Remove 
        {
            bool changed = false;
            Session? previousSession = null;

            foreach (var session in _sessions.SessionList.OrderBy(se => se.FirstDateTime).ToList())
            {
                if (previousSession != null)
                {
                    if (previousSession.Mode == Modes.Discharging && session.Mode == Modes.Discharging)
                    {
                        if (previousSession.IsMoreProfitable(session))
                        {
                            _sessions.RemoveSession(previousSession);
                            changed = true;
                        }
                        else
                        {
                            _sessions.RemoveSession(session);
                            changed = true;
                        }
                    }
                }

                previousSession = session;
            }

            return changed;
        }

        /// <summary>
        /// Check if discharging sessions are profitable.
        /// </summary>
        private bool CheckProfitability()
        {
            bool changed = false;

            Session? lastSession = null;

            foreach (var session in _sessions.SessionList.OrderBy(se => se.FirstDateTime))
            {
                if (lastSession != null)
                {
                    if (lastSession.Mode == Modes.Charging && session.Mode == Modes.Discharging)
                    {
                        using var chargeEnumerator = lastSession.GetQuarterlyInfoList().OrderBy(hi => hi.Price).ToList().GetEnumerator();
                        using var dischargeEnumerator = session.GetQuarterlyInfoList().OrderByDescending(hi => hi.Price).ToList().GetEnumerator();

                        var hasCharging = chargeEnumerator.MoveNext();
                        var hasDischarging = dischargeEnumerator.MoveNext();

                        while (hasCharging && hasDischarging)
                        {
                            if (chargeEnumerator.Current.Price + _settingsConfig.CycleCost > dischargeEnumerator.Current.Price)
                            {
                                session.RemoveQuarterlyInfo(dischargeEnumerator.Current);

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

                lastSession = session;
            }

            return changed;
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
        private bool ShrinkSessions()
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

            return changed;
        }

        /// <summary>
        /// This routine detects charge session that follow each other and determines how much
        /// charge is needed for the session.
        /// </summary>
        private void CalculateChargeNeeded()
        {
            Session? previousSession = null;
            var totalCapacity = _batteryContainer.GetTotalCapacity();

            foreach (var nextSession in _sessions.SessionList.OrderBy(se => se.FirstDateTime))
            {
                if (previousSession != null)
                {
                    _sessions.SetEstimateChargeNeededUntilNextSession(previousSession, nextSession);
                }

                previousSession = nextSession;
            }

            if (previousSession != null)
            {
                _sessions.SetEstimateChargeNeededUntilNextSession(previousSession);
            }
        }

        /// <summary>
        /// Determine when the prices are the highest en the lowest.
        /// </summary>
        private void CreateSessions()
        {
            _sessions?.Dispose();

            _sessions = null;

            if (quarterlyInfos != null && quarterlyInfos.Count > 0)
            {
                var loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

                _sessions = new Sessions(quarterlyInfos,
                                         _settingsConfig,
                                         _batteryContainer!,
                                         _timeZoneService,
                                         _financialResultsService,
                                         _consumptionDataService,
                                         _consumptionMonitorService,
                                         _energyHistoryDataService,
                                         loggerFactory);

                var list = quarterlyInfos.OrderBy(hi => hi.Time).ToList();

                // Check the first element
                if (list.Count > 1)
                {
                    var currentPrice = list[0].SmoothedBuyingPrice;
                    var nextPrice = list[1].SmoothedBuyingPrice;

                    if (currentPrice <= nextPrice)
                    {
                        if (!_sessions.InAnySession(list[0]))
                            _sessions.AddNewSession(Modes.Charging, list[0]);
                    }

                    currentPrice = list[0].SmoothedSellingPrice;
                    nextPrice = list[1].SmoothedSellingPrice;

                    if (currentPrice >= nextPrice)
                    {
                        if (!_sessions.InAnySession(list[0]))
                            _sessions.AddNewSession(Modes.Discharging, list[0]);
                    }
                }

                // Check the elements in between.
                for (var index = 1; index < list.Count - 2; index++)
                {
                    var currentPrice = list[index].SmoothedBuyingPrice;
                    var previousPrice = list[index - 1].SmoothedBuyingPrice;
                    var nextPrice = list[index + 1].SmoothedBuyingPrice;

                    if (currentPrice <= previousPrice && currentPrice <= nextPrice)
                    {
                        if (!_sessions.InAnySession(list[index]))
                            _sessions.AddNewSession(Modes.Charging, list[index]);
                    }

                    currentPrice = list[index].SmoothedSellingPrice;
                    previousPrice = list[index - 1].SmoothedSellingPrice;
                    nextPrice = list[index + 1].SmoothedSellingPrice;

                    if (currentPrice >= previousPrice && currentPrice >= nextPrice)
                    {
                        if (!_sessions.InAnySession(list[index]))
                            _sessions.AddNewSession(Modes.Discharging, list[index]);
                    }
                }

                // Skipping the last element.

                _sessions.CompleteAllSessions();
            }
            else
                _logger.LogWarning("QuarterlyInfos is empty!!");
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _settingsConfigSubscription.Dispose();
                _sessyBatteryConfigSubscription.Dispose();

                quarterlyInfos.Clear();
                quarterlyInfos = null;
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

        public string GetBatteryMode()
        {
            var quarterlyInfo = _sessions.GetCurrentQuarterlyInfo();

            switch (quarterlyInfo.Mode)
            {
                case Modes.Unknown:
                    return "?";
                case Modes.Charging:
                    return "Charging";
                case Modes.Discharging:
                    return "Discharging";
                case Modes.ZeroNetHome:
                    return "Zero net home";
                default:
                    return "?";
            }
        }
    }
}
