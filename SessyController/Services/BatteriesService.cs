using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Managers;
using SessyController.Services.InverterServices;
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

        private LoggingService<BatteriesService> _logger { get; set; }

        private static List<HourlyInfo>? hourlyInfos { get; set; } = new List<HourlyInfo>();

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
            if (_dayAheadMarketService.PricesInitialized)
            {
                // Prevent race conditions.
                HourlyInfoSemaphore.Wait();

                try
                {
                    DetermineChargingHours();

                    if (_sessions != null)
                    {
                        _solarService.GetExpectedSolarPower(hourlyInfos!);

                        await EvaluateSessions();

                        await AreWeInControl();

                        if (WeAreInControl)
                        {
                            HourlyInfo? currentHourlyInfo = _sessions.GetCurrentHourlyInfo();

                            if (currentHourlyInfo != null)
                            {
                                var batteryStates = await GetBatteryStates(currentHourlyInfo);

                                await HandleChargingAndDischarging(batteryStates, currentHourlyInfo);
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

                    DataChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Checks who has control. If control changed since the last store of data a new record is stored.
        /// </summary>
        private async Task AreWeInControl()
        {
            WeAreInControl = !await SupplierIsControllingTheBatteries();

            SessyWebControlStatus status = WeAreInControl ? SessyWebControlStatus.SessyWeb : SessyWebControlStatus.Provider;

            var last = _sessyWebControlDataService.Get((set) =>
            {
                return set.OrderByDescending(sc => sc.Time)
                        .FirstOrDefault();
            });

            if (last == null || last.Status != status)
            {
                StoreStatus(status);
            }
        }

        /// <summary>
        /// Store a new control record to the database.
        /// </summary>
        private void StoreStatus(SessyWebControlStatus status)
        {
            var controlList = new List<SessyWebControl>
            {
                new SessyWebControl
                {
                    Time = _timeZoneService.Now,
                    Status = status
                }
            };

            _sessyWebControlDataService.Add(controlList);
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
        private async Task HandleChargingAndDischarging(BatteryStates batteryStates, HourlyInfo currentHourlyInfo)
        {
            if ((_dayAheadMarketService.PricesAvailable) && _settingsConfig.ManualOverride == false)
            {
                await HandleAutomaticCharging(_sessions!, currentHourlyInfo, batteryStates);
            }
            else
            {
                _logger.LogInformation("No prices available from ENTSO-E, switching to manual charging");

                HandleManualCharging(currentHourlyInfo);
            }
        }

        /// <summary>
        /// Returns the fetched prices and analyzes them.
        /// </summary>
        public List<HourlyInfo>? GetHourlyInfos()
        {
            HourlyInfoSemaphore.Wait();

            try
            {
                return hourlyInfos;
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

        private async Task HandleAutomaticCharging(Sessions sessions, HourlyInfo currentHourlyInfo, BatteryStates batteryStates)
        {
            await CancelSessionIfStateRequiresIt(sessions, currentHourlyInfo, batteryStates);

            var currentSession = sessions.GetSession(currentHourlyInfo);

#if !DEBUG
            switch (currentHourlyInfo.Mode)
            {
                case Modes.Charging:
                    {
                        var chargingPower = currentSession.GetChargingPowerInWatts(currentHourlyInfo);
                        _batteryContainer.StartCharging(chargingPower);
                        break;
                    }

                case Modes.Discharging:
                    {
                        var chargingPower = currentSession.GetChargingPowerInWatts(currentHourlyInfo);
                        _batteryContainer.StartDisharging(chargingPower);
                        break;
                    }

                case Modes.ZeroNetHome:
                    {
                        _batteryContainer.StartNetZeroHome();
                        break;
                    }

                case Modes.Disabled:
                case Modes.Unknown:
                default:
                    {
                        _batteryContainer.StopAll();
                        break;
                    }

            }
#endif
        }

        private void HandleManualCharging(HourlyInfo currentHourlyInfo)
        {
#if !DEBUG
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                _batteryContainer.StartCharging(_batteryContainer.GetChargingCapacityPerQuarter());
            else if (_settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                _batteryContainer.StartDisharging(_batteryContainer.GetDischargingCapacityPerQuarter());
            else if (_settingsConfig.ManualNetZeroHomeHours.Contains(localTime.Hour))
                _batteryContainer.StartNetZeroHome();
            else
                _batteryContainer.StopAll();
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

            public bool IsInsideTimeFrame(HourlyInfo hourlyInfo)
            {
                return FirstDate <= hourlyInfo.Time && LastDate >= hourlyInfo.Time;
            }
        }

        private SessionInfo? _lastSessionCanceled { get; set; } = null;

        /// <summary>
        /// If batteries are full (enough) stop charging this session
        /// If batteries are empty stop discharging this session
        /// </summary>
        private async Task CancelSessionIfStateRequiresIt(Sessions sessions, HourlyInfo currentHourlyInfo, BatteryStates batteryStates)
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

                if (currentHourlyInfo.Charging)
                {
                    if (batteryStates.BatteriesAreFull || batteryStates.ChargeAtMaximum)
                    {
                        StopSession(session);
                        _logger.LogWarning($"Warning: Charging session stopped because batteries are full (enough). {session}, batteries are full: {batteryStates.BatteriesAreFull}, charge at maximum: {batteryStates.ChargeAtMaximum}");
                    }
                }
                else if (currentHourlyInfo.Discharging)
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
        private async Task<BatteryStates> GetBatteryStates(HourlyInfo currentHourlyInfo)
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
        private async Task<bool> IsMinChargeNeededReached(HourlyInfo currentHourlyInfo)
        {
            var currentChargeState = await _batteryContainer.GetStateOfChargeInWatts();

            return (currentHourlyInfo.Discharging && currentChargeState <= currentHourlyInfo.ChargeNeeded);
        }

        /// <summary>
        /// Check whether the current hourly info is in a charging session and return
        /// true if so and the calculated max charge needed is reached.
        /// </summary>
        private async Task<bool> IsMaxChargeNeededReached(HourlyInfo currentHourlyInfo)
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
        public void DetermineChargingHours()
        {
            DateTime localTime = _timeZoneService.Now;

            if (FetchPricesFromENTSO_E(localTime))
            {
                GetChargingHours();
            }
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        private bool FetchPricesFromENTSO_E(DateTime localTime)
        {
            // Get the available hourly prices.
            hourlyInfos = _dayAheadMarketService.GetPrices()
                .OrderBy(hp => hp.Time)
                .ToList();

            HourlyInfo.AddSmoothedPrices(hourlyInfos, 6);

            return hourlyInfos != null && hourlyInfos.Count > 0;
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

                hourlyInfos = hourlyInfos!
                    .OrderBy(hp => hp.Time)
                    .ToList();

                CreateSessions();

                if (_sessions != null)
                {
                    RemoveExtraChargingSessions();

                    CheckSessions();
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"Unhandled exception in GetChargingHours {ex.ToDetailedString()}");
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
                if (session.GetHourlyInfoList().Count() == 0)
                    throw new InvalidOperationException($"Session without HourlyInfos");
            }

            foreach (var session in _sessions.SessionList)
            {
                switch (session.Mode)
                {
                    case Modes.Charging:
                        if (!session.GetHourlyInfoList().All(hi => hi.Mode == Modes.Charging))
                            throw new InvalidOperationException($"Charging session has hourlyinfo objects without charging mode {session}");

                        break;

                    case Modes.Discharging:
                        if (!session.GetHourlyInfoList().All(hi => hi.Mode == Modes.Discharging))
                            throw new InvalidOperationException($"Discharging session has hourlyinfo objects without discharging mode {session}");

                        break;

                    case Modes.Unknown:
                    case Modes.ZeroNetHome:
                    case Modes.Disabled:
                    default:
                        throw new InvalidOperationException($"Session has wrong mode {session}");
                }
            }

            foreach (var hourlyInfo in hourlyInfos)
            {
                switch (hourlyInfo.Mode)
                {
                    case Modes.Charging:
                        {
                            if (!_sessions.InAnySession(hourlyInfo))
                                throw new InvalidOperationException($"Info not in a session {hourlyInfo}");

                            Session session = CheckSession(_sessions, hourlyInfo);

                            if (session.Mode != Modes.Charging)
                                throw new InvalidOperationException($"Charging info in wrong session {hourlyInfo}");

                            break;
                        }

                    case Modes.Discharging:
                        {
                            if (!_sessions.InAnySession(hourlyInfo))
                                throw new InvalidOperationException($"Info not in a session {hourlyInfo}");

                            Session session = CheckSession(_sessions, hourlyInfo);

                            if (session.Mode != Modes.Discharging)
                                throw new InvalidOperationException($"Discharging info in wrong session {hourlyInfo}");

                            break;
                        }

                    case Modes.ZeroNetHome:
                    case Modes.Disabled:
                    default:
                        if (_sessions.InAnySession(hourlyInfo))
                        {
                            throw new InvalidOperationException($"Hourlyinfo should not be in any session {hourlyInfo}");
                        }

                        break;
                }
            }
        }

        private static Session CheckSession(Sessions sessions, HourlyInfo hourlyInfo)
        {
            var session = sessions.FindSession(hourlyInfo);

            if (session.GetHourlyInfoList().Count < 1)
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
            double quarterNeed = _settingsConfig.EnergyNeedsPerMonth / 96; // Per quarter hour
            double chargingCapacity = _sessyBatteryConfig.TotalChargingCapacity / 4.0; // Per quarter hour
            double dischargingCapacity = _sessyBatteryConfig.TotalDischargingCapacity / 4.0; // Per quarter hour
            var now = _timeZoneService.Now;
            var localTimeHour = now.Date.AddHours(now.Hour);
            charge = await _batteryContainer.GetStateOfChargeInWatts();

            var hourlyInfoList = hourlyInfos!
                .OrderBy(hp => hp.Time)
                .ToList();

            hourlyInfoList.ForEach(hi => hi.ChargeLeft = charge);

            foreach (var hourlyInfo in hourlyInfoList.Where(hi => hi.Time >= now.DateFloorQuarter()))
            {
                var session = _sessions.GetSession(hourlyInfo);

                switch (hourlyInfo.Mode)
                {
                    case Modes.Charging:
                        {
                            if (charge < hourlyInfo.ChargeNeeded)
                            {
                                var toCharge = hourlyInfo.ChargeNeeded - charge;

                                charge += Math.Min(toCharge, chargingCapacity);
                            }

                            break;
                        }

                    case Modes.Discharging:
                        {
                            if (charge > hourlyInfo.ChargeNeeded)
                            {
                                var toDischarge = charge - hourlyInfo.ChargeNeeded;

                                charge -= Math.Min(toDischarge, dischargingCapacity);
                            }

                            break;
                        }

                    case Modes.ZeroNetHome:
                        {
                            charge -= quarterNeed;
                            charge += hourlyInfo.SolarPowerInWatts;

                            break;
                        }

                    case Modes.Disabled:
                        break;

                    default:
                        throw new InvalidOperationException($"Invalid mode for hourlyInfo: {hourlyInfo}");
                }

                if (charge < 0) charge = 0.0;
                if (charge > totalCapacity) charge = totalCapacity;

                hourlyInfo.ChargeLeft = charge;
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
            bool changed3;
            bool changed4;

            do
            {
                changed1 = false;
                changed2 = false;
                changed3 = false;
                changed4 = false;

                changed1 = RemoveExtraQuarters();

                changed2 = RemoveEmptySessions();

                changed3 = CheckProfitability();

                changed4 = RemoveEmptySessions();

            } while (changed1 || changed2 || changed3 || changed4);
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
                        using var chargeEnumerator = lastSession.GetHourlyInfoList().OrderBy(hi => hi.BuyingPrice).ToList().GetEnumerator();
                        using var dischargeEnumerator = session.GetHourlyInfoList().OrderByDescending(hi => hi.BuyingPrice).ToList().GetEnumerator();

                        var hasCharging = chargeEnumerator.MoveNext();
                        var hasDischarging = dischargeEnumerator.MoveNext();

                        while (hasCharging && hasDischarging)
                        {
                            if (chargeEnumerator.Current.BuyingPrice + _settingsConfig.CycleCost > dischargeEnumerator.Current.SellingPrice)
                            {
                                session.RemoveHourlyInfo(dischargeEnumerator.Current);

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
        /// Remove quarters outside the max quarters.
        /// </summary>
        private bool RemoveExtraQuarters()
        {
            var changed = false;

            List<HourlyInfo> listToLimit;

            foreach (var session in _sessions.SessionList.ToList())
            {
                switch (session.Mode)
                {
                    case Modes.Charging:
                        {
                            listToLimit = session.GetHourlyInfoList()
                                .OrderByDescending(hp => hp.BuyingPrice)
                                .ThenBy(hp => hp.Time)
                                .ToList();

                            break;
                        }

                    case Modes.Discharging:
                        {
                            listToLimit = session.GetHourlyInfoList()
                                .OrderBy(hp => hp.SellingPrice)
                                .ThenBy(hp => hp.Time)
                                .ToList();

                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Unknown mode {session.Mode}");
                }

                for (int i = session.MaxQuarters; i < listToLimit.Count; i++)
                {
                    session.RemoveHourlyInfo(listToLimit[i]);
                    changed = true;
                }
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
                if (session.GetHourlyInfoList().Count() == 0)
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
                var maxQuarters = session.GetHoursForMode(); // Per quarter hour

                if (session.RemoveAllAfter(maxQuarters))
                {
                    changed = true;
                }
            }

            RemoveEmptySessions();

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

            _sessions.SessionList.ToList().ForEach(se => se.SetChargeNeeded(totalCapacity));

            foreach (var nextSession in _sessions.SessionList.OrderBy(se => se.FirstDateTime))
            {
                if (previousSession != null)
                {
                    switch (previousSession.Mode)
                    {
                        case Modes.Charging:
                            HandleChargingCalculation(previousSession, nextSession);
                            break;

                        case Modes.Discharging:
                            HandleDischargingCalculation(previousSession, nextSession);
                            break;

                        default:
                            throw new InvalidOperationException($"Wrong mode: {previousSession}");
                    }

                    HandleHoursBetweenSessions(previousSession, nextSession);
                }

                previousSession = nextSession;
            }

            if (previousSession != null)
            {
                HandleLastSession(previousSession);
            }
        }

        private void HandleHoursBetweenSessions(Session previousSession, Session nextSession)
        {
            var hourlyInfosBetween = _sessions.GetInfoObjectsBetween(previousSession, nextSession);
        }

        private void HandleLastSession(Session previousSession)
        {
            switch (previousSession.Mode)
            {
                case Modes.Charging:
                    HandleLastChargingSession(previousSession);
                    break;

                case Modes.Discharging:
                    HandleLastDischargingSession(previousSession);
                    break;

                default:
                    throw new InvalidOperationException($"Wrong mode: {previousSession}");
            }
        }

        private void HandleLastChargingSession(Session session)
        {
            var totalCapacity = _batteryContainer.GetTotalCapacity();
            var hourlyInfoObjectsAfter = _sessions.GetInfoObjectsAfter(session);

            session.SetChargeNeeded(totalCapacity);
            hourlyInfoObjectsAfter.ForEach(hi => hi.ChargeNeeded = totalCapacity);
        }

        private void HandleLastDischargingSession(Session previousSession)
        {
            var hourlyInfosAfter = _sessions.GetInfoObjectsAfter(previousSession);
            var needed = GetEstimatePowerNeeded(hourlyInfosAfter);

            previousSession.SetChargeNeeded(needed);

            hourlyInfosAfter.ForEach(hi => hi.ChargeNeeded = needed);
        }

        /// <summary>
        /// The previous session is a charging session, handle it.
        /// </summary>
        private void HandleChargingCalculation(Session previousSession, Session nextSession)
        {
            switch (nextSession.Mode)
            {
                case Modes.Charging:
                    HandleChargingChargingSessions(previousSession, nextSession);
                    break;

                case Modes.Discharging:
                    HandleChargingDischargingSessions(previousSession, nextSession);
                    break;

                default:
                    throw new InvalidOperationException($"Wrong mode: {previousSession}");
            }
        }

        /// <summary>
        /// The previous session is a discharging session. Handle it.
        /// </summary>
        private void HandleDischargingCalculation(Session previousSession, Session nextSession)
        {
            switch (nextSession.Mode)
            {
                case Modes.Charging:
                    HandleDischargingChargingCalculation(previousSession, nextSession);
                    break;

                case Modes.Discharging:
                    HandleDischargingDischarging(previousSession, nextSession);
                    break;

                default:
                    throw new InvalidOperationException($"Wrong mode: {previousSession}");
            }
        }

        private void HandleDischargingDischarging(Session previousSession, Session nextSession)
        {
            List<HourlyInfo> infoObjectsBetween = _sessions.GetInfoObjectsBetween(previousSession, nextSession);
            var estimateNeeded = GetEstimatePowerNeeded(infoObjectsBetween);

            var chargeNeeded = Math.Min(estimateNeeded, _batteryContainer.GetTotalCapacity());

            previousSession.SetChargeNeeded(chargeNeeded);
            infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = chargeNeeded);
        }

        /// <summary>
        /// The previous session is a discharging session. The next is a charging session. Handle it.
        /// </summary>
        private void HandleDischargingChargingCalculation(Session previousSession, Session nextSession)
        {
            List<HourlyInfo> infoObjectsBetween = _sessions.GetInfoObjectsBetween(previousSession, nextSession);
            var totalNeed = GetEstimatePowerNeeded(infoObjectsBetween);

            previousSession.SetChargeNeeded(totalNeed);
            infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = totalNeed);
        }

        /// <summary>
        /// The previous session is a charging session. The next a discharging session. Handle it.
        /// </summary>
        private void HandleChargingDischargingSessions(Session previousSession, Session nextSession)
        {
            var chargeNeeded = _batteryContainer.GetTotalCapacity(); ;
            double quarterNeed = _settingsConfig.EnergyNeedsPerMonth / 96; // Per quarter hour

            List<HourlyInfo> infoObjectsBetween = _sessions.GetInfoObjectsBetween(previousSession, nextSession);

            var solarPower = infoObjectsBetween.Where(oi => oi.NetZeroHomeWithSolar).Sum(io => io.SolarPowerInWatts);

            chargeNeeded -= solarPower;
            chargeNeeded += infoObjectsBetween.Count * quarterNeed;

            chargeNeeded = EnsureBoundaries(chargeNeeded);

            previousSession.SetChargeNeeded(chargeNeeded);
            infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = chargeNeeded);
        }

        /// <summary>
        /// Ensure the power is positive and less or equal to the total capacity.
        /// </summary>
        private double EnsureBoundaries(double charge)
        {
            var totalCapacity = _batteryContainer.GetTotalCapacity();

            charge = Math.Max(0.0, charge); // Prevent negative power
            charge = Math.Min(charge, totalCapacity); // Prevent charge to be bigger than capacity.

            return charge;
        }

        /// <summary>
        /// Both sessions are charging sessions. Determine the charge needed for the previous session.
        /// </summary>
        private void HandleChargingChargingSessions(Session previousSession, Session nextSession)
        {
            var totalCapacity = _batteryContainer.GetTotalCapacity();
            List<HourlyInfo> infoObjectsBetween = _sessions.GetInfoObjectsBetween(previousSession, nextSession);

            if (previousSession.IsMoreProfitable(nextSession))
            {
                previousSession.SetChargeNeeded(totalCapacity);
                infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = totalCapacity);
            }
            else
            {
                if (infoObjectsBetween.Any())
                {
                    var chargeNeeded = GetEstimatePowerNeeded(infoObjectsBetween);

                    infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = Math.Min(_batteryContainer.GetTotalCapacity(), chargeNeeded));
                    previousSession.SetChargeNeeded(chargeNeeded);
                }
                else
                {
                    previousSession.SetChargeNeeded(0.0);
                }
            }
        }

        private double GetEstimatePowerNeeded(List<HourlyInfo> infoObjectsBetween)
        {
            double power = 0.0;
            var totalCapacity = _batteryContainer.GetTotalCapacity();
            var requiredEnergyPerQuarter = _settingsConfig.EnergyNeedsPerMonth / 96.0; // Per quarter hour

            foreach (var hourlyInfo in infoObjectsBetween)
            {
                if (hourlyInfo.NetZeroHomeWithoutSolar)
                {
                    // TODO: The estimate is incorrect. It calculates the net power from the grid (Zero Net Home), not the needs of the home.
                    //var temperature = _weatherService.GetTemperature(hourlyInfo.Time);

                    //if (temperature != null)
                    //{
                    //    power += _powerEstimatesService.GetPowerEstimate(hourlyInfo.Time, temperature.Value);
                    //}
                    //else
                    //{
                    power += requiredEnergyPerQuarter;
                    //}
                }
            }

            return EnsureBoundaries(power);
        }

        /// <summary>
        /// Determine when the prices are the highest en the lowest.
        /// </summary>
        private void CreateSessions()
        {
            _sessions?.Dispose();

            _sessions = null;

            if (hourlyInfos != null && hourlyInfos.Count > 0)
            {
                var loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

                _sessions = new Sessions(hourlyInfos,
                                         _settingsConfig,
                                         _batteryContainer!,
                                         _timeZoneService,
                                         _financialResultsService,
                                         loggerFactory);

                var list = hourlyInfos.OrderBy(hi => hi.Time).ToList();

                // Check the first element
                if (list.Count > 1)
                {
                    if (list[0].SmoothedPrice <= list[1].SmoothedPrice)
                    {
                        if (!_sessions.InAnySession(list[0]))
                            _sessions.AddNewSession(Modes.Charging, list[0]);
                    }

                    if (list[0].SmoothedPrice >= list[1].SmoothedPrice)
                    {
                        if (!_sessions.InAnySession(list[0]))
                            _sessions.AddNewSession(Modes.Discharging, list[0]);
                    }
                }

                // Check the elements in between.
                for (var index = 1; index < list.Count - 2; index++)
                {
                    if (list[index].SmoothedPrice < list[index - 1].SmoothedPrice && list[index].SmoothedPrice <= list[index + 1].SmoothedPrice)
                    {
                        if (!_sessions.InAnySession(list[index]))
                            _sessions.AddNewSession(Modes.Charging, list[index]);
                    }

                    if (list[index].SmoothedPrice > list[index - 1].SmoothedPrice && list[index].SmoothedPrice >= list[index + 1].SmoothedPrice)
                    {
                        if (!_sessions.InAnySession(list[index]))
                            _sessions.AddNewSession(Modes.Discharging, list[index]);
                    }
                }

                // Skipping the last element.
            }
            else
                _logger.LogWarning("HourlyInfos is empty!!");
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _settingsConfigSubscription.Dispose();
                _sessyBatteryConfigSubscription.Dispose();

                hourlyInfos.Clear();
                hourlyInfos = null;
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
            var hourlyInfo = _sessions.GetCurrentHourlyInfo();

            switch (hourlyInfo.Mode)
            {
                case Modes.Unknown:
                    return "?";
                case Modes.Charging:
                    return "Charging";
                case Modes.Discharging:
                    return "Discharging";
                case Modes.ZeroNetHome:
                    return "Zero net home";
                case Modes.Disabled:
                    return "Disabled";
                default:
                    return "?";
            }
        }
    }
}
