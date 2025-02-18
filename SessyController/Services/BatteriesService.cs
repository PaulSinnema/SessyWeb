using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Services.Items;
using static SessyController.Services.Items.Session;

namespace SessyController.Services
{
    /// <summary>
    /// This service maintains all batteries in the system.
    /// </summary>
    public partial class BatteriesService : BackgroundService
    {
        private IServiceScope _scope { get; set; }
        private SessyService? _sessyService { get; set; }
        private P1MeterService? _p1MeterService { get; set; }
        private DayAheadMarketService? _dayAheadMarketService { get; set; }
        private SolarEdgeService? _solarEdgeService { get; set; }
        private SolarService? _solarService { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }
        private SettingsConfig _settingsConfig { get; set; }
        private IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor { get; set; }
        private SessyBatteryConfig _sessyBatteryConfig { get; set; }
        private BatteryContainer? _batteryContainer { get; set; }
        private TimeZoneService? _timeZoneService { get; set; }
        private LoggingService<BatteriesService> _logger { get; set; }

        private static List<HourlyInfo>? hourlyInfos { get; set; } = new List<HourlyInfo>();
        private bool _settingsChanged = false;

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

            _settingsConfigMonitor.OnChange((SettingsConfig settings) =>
            {
                _settingsConfig = settings;
                _settingsChanged = true;
            });
            _sessyBatteryConfigMonitor.OnChange((SessyBatteryConfig settings) =>
            {
                _sessyBatteryConfig = settings;
                _settingsChanged = true;
            });

            _settingsConfig = settingsConfigMonitor.CurrentValue;
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue;

            if (_settingsConfig == null) throw new InvalidOperationException("ManagementSettings missing");
            if (_sessyBatteryConfig == null) throw new InvalidOperationException("Sessy:Batteries missing");

            _scope = _serviceScopeFactory.CreateScope();

            _sessyService = _scope.ServiceProvider.GetRequiredService<SessyService>();
            _p1MeterService = _scope.ServiceProvider.GetRequiredService<P1MeterService>();
            _dayAheadMarketService = _scope.ServiceProvider.GetRequiredService<DayAheadMarketService>();
            _solarEdgeService = _scope.ServiceProvider.GetRequiredService<SolarEdgeService>();
            _solarService = _scope.ServiceProvider.GetRequiredService<SolarService>();
            _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
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
            _logger.LogInformation("EPEXHourlyInfosService started.");

            // Loop to fetch prices every hour
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
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

            _logger.LogInformation("BatteriesService stopped.");
        }

        private SemaphoreSlim HourlyInfoSemaphore = new SemaphoreSlim(1);

        private Sessions? sessions { get; set; } = null;

        /// <summary>
        /// This routine is called periodicly as a background task.
        /// </summary>
        public async Task Process(CancellationToken cancellationToken)
        {
            if (_dayAheadMarketService.PricesInitialized)
            {
                HourlyInfoSemaphore.Wait();

                try
                {
                    sessions = DetermineChargingHours(sessions!);

                    if (sessions != null)
                    {
                        _solarService.GetExpectedSolarPower(hourlyInfos);

                        await EvaluateSessions(sessions, hourlyInfos);

                        HourlyInfo? currentHourlyInfo = GetCurrentHourlyInfo();

                        if (!(_dayAheadMarketService.PricesAvailable && currentHourlyInfo != null))
                        {
                            _logger.LogWarning("No prices available from ENTSO-E, switching to manual charging");

                            HandleManualCharging();
                        }
                        else
                        {
                            await HandleAutomaticCharging(sessions);
                        }
                    }
                }
                finally
                {
                    HourlyInfoSemaphore.Release();
                }
            }
        }

        /// <summary>
        /// Returns the fetche and analyzed hourly prices.
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

        private async Task HandleAutomaticCharging(Sessions? sessions)
        {
            HourlyInfo? currentHourlyInfo = GetCurrentHourlyInfo();

            if (currentHourlyInfo != null)
            {
                await CancelSessionIfStateRequiresIt(sessions, currentHourlyInfo);
#if !DEBUG

                if (currentHourlyInfo.Charging)
                    _batteryContainer.StartCharging();
                else if (currentHourlyInfo.Discharging)
                    _batteryContainer.StartDisharging();
                else if (currentHourlyInfo.ZeroNetHome)
                    _batteryContainer.StartNetZeroHome();
                else
                    _batteryContainer.StopAll();        
#endif
            }
        }

        private void HandleManualCharging()
        {
#if !DEBUG
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                _batteryContainer.StartCharging();
            else if (_settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                _batteryContainer.StartDisharging();
            else if (_settingsConfig.ManualNetZeroHomeHours.Contains(localTime.Hour))
                _batteryContainer.StartNetZeroHome();
            else
                _batteryContainer.StopAll();
#endif
        }

        private HourlyInfo? GetCurrentHourlyInfo()
        {
            var localTime = _timeZoneService.Now;

            return hourlyInfos?
                .FirstOrDefault(hp => hp.Time.Date == localTime.Date && hp.Time.Hour == localTime.Hour);
        }

        /// <summary>
        /// If batteries are full stop charging this session
        /// If batteries are empty stop discharging this session
        /// </summary>
        private async Task CancelSessionIfStateRequiresIt(Sessions? sessions, HourlyInfo currentHourlyInfo)
        {
            if (currentHourlyInfo.Charging)
            {
                bool batteriesAreFull = await AreAllBattiesFull(currentHourlyInfo);
                bool chargeIsEnough = await IsMaxChargeNeededReached(currentHourlyInfo);

                if (batteriesAreFull || chargeIsEnough)
                {
                    StopChargingSession(sessions, currentHourlyInfo);
                    _logger.LogWarning("Warning: Charging session stopped because batteries are full (enough).");
                }
            }
            else if (currentHourlyInfo.Discharging)
            {
                bool batteriesAreEmpty = await AreAllBattiesEmpty(currentHourlyInfo);

                if (batteriesAreEmpty)
                {
                    StopDischargingSession(sessions, currentHourlyInfo);
                    _logger.LogWarning("Warning: Discharging session stopped because batteries are empty.");
                }
            }
        }

        private async Task<bool> IsMaxChargeNeededReached(HourlyInfo currentHourlyInfo)
        {
            var session = sessions.GetSession(currentHourlyInfo);

            if (session.MaxChargeNeeded > 0.0)
            {
                var currentChargeState = await _batteryContainer.GetStateOfChargeInWatts();

                if (session.MaxChargeNeeded < currentChargeState)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Cancel charging for current and future consecutive charging hours.
        /// </summary>
        private static void StopChargingSession(Sessions? sessions, HourlyInfo currentHourlyInfo)
        {
            if (hourlyInfos != null)
            {
                var enumPrices = hourlyInfos.GetEnumerator();

                if (enumPrices.MoveNext())
                    while (enumPrices.Current.Time < currentHourlyInfo.Time)
                        if (!enumPrices.MoveNext())
                            return;

                while (enumPrices.Current.Charging)
                {
                    sessions.RemoveFromSession(enumPrices.Current); // Stop charging

                    if (!enumPrices.MoveNext())
                        return;
                }
            }
        }

        /// <summary>
        /// Cancel discharging for current and future consecutive discharging hours.
        /// </summary>
        private static void StopDischargingSession(Sessions? sessions, HourlyInfo currentHourlyInfo)
        {
            if (hourlyInfos != null)
            {
                var enumPrices = hourlyInfos.GetEnumerator();

                if (enumPrices.MoveNext())
                    while (enumPrices.Current.Time < currentHourlyInfo.Time)
                        if (!enumPrices.MoveNext())
                            return;

                while (enumPrices.Current.Discharging)
                {
                    sessions.RemoveFromSession(enumPrices.Current); // Stop discharging

                    if (!enumPrices.MoveNext())
                        return;
                }
            }
        }

        /// <summary>
        /// Returns true if all batteries have state SYSTEM_STATE_BATTERY_FULL
        /// </summary>
        private async Task<bool> AreAllBattiesFull(HourlyInfo currentHourlyInfo)
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
        private async Task<bool> AreAllBattiesEmpty(HourlyInfo currentHourlyInfo)
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
        public Sessions? DetermineChargingHours(Sessions sessions)
        {
            DateTime localTime = _timeZoneService.Now;

            if (FetchPricesFromENTSO_E(localTime))
            {
                sessions = GetChargingHours();
            }

            return sessions;
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// This routine gets info objects or adds missing ones.
        /// </summary>
        private bool FetchPricesFromENTSO_E(DateTime localTime)
        {
            // Get the available hourly prices from now.
            var fetchedPrices = _dayAheadMarketService.GetPrices()
                .OrderBy(hp => hp.Time)
                .ToList();

            if (hourlyInfos == null) // First time fetch
            {
                hourlyInfos = fetchedPrices;
            }
            else
            {
                // There are already info objects present, supplement with missing.
                foreach (var hourlyInfo in fetchedPrices)
                {
                    if (!hourlyInfos.Any(hp => hp.Time == hourlyInfo.Time))
                    {
                        hourlyInfos.Add(hourlyInfo);
                    }
                }

                HourlyInfo.AddSmoothedPrices(hourlyInfos, 3);
            }

            var maxTime = localTime.Date.AddDays(-1);

            // Remove yesterdays info objects.
            hourlyInfos.RemoveAll(hi => hi.Time < maxTime);

            return hourlyInfos != null && hourlyInfos.Count > 0;
        }

        private DateTime? lastSessionCreationDate { get; set; } = null;

        /// <summary>
        /// In this routine it is determined when to charge the batteries.
        /// </summary>
        private Sessions GetChargingHours()
        {
            DateTime now = _timeZoneService.Now;

            hourlyInfos = hourlyInfos.OrderBy(hp => hp.Time)
                .ToList();

            DateTime currentSessionCreationDate = hourlyInfos.Max(hi => hi.Time);

            if (lastSessionCreationDate == null ||
                lastSessionCreationDate != currentSessionCreationDate ||
                _settingsChanged)
            {
                lastSessionCreationDate = currentSessionCreationDate;

                Sessions localSessions = CreateSessions(hourlyInfos);

                RemoveExtraChargingSessions(localSessions);
#if DEBUG
                CheckSessions(hourlyInfos, localSessions);
#endif

                _settingsChanged = false;

                return localSessions;
            }

            return sessions!;
        }

#if DEBUG
        /// <summary>
        /// This method is voor debugging purposes only. It checks the content of hourlyInfos
        /// and sessions.
        /// </summary>
        private void CheckSessions(List<HourlyInfo> hourlyInfos, Sessions sessions)
        {
            foreach (var hourlyInfo in hourlyInfos)
            {
                switch ((hourlyInfo.Charging, hourlyInfo.Discharging, hourlyInfo.ZeroNetHome))
                {
                    case (true, false, false): // Charging
                        {
                            if (!sessions.InAnySession(hourlyInfo))
                                throw new InvalidOperationException($"Info not in a session {hourlyInfo}");

                            Session session = CheckSession(sessions, hourlyInfo);

                            if (session.Mode != Modes.Charging)
                                throw new InvalidOperationException($"Charging info in wrong session {hourlyInfo}");

                            break;
                        }

                    case (false, true, false): // Discharging
                        {
                            if (!sessions.InAnySession(hourlyInfo))
                                throw new InvalidOperationException($"Info not in a session {hourlyInfo}");

                            Session session = CheckSession(sessions, hourlyInfo);

                            if (session.Mode != Modes.Discharging)
                                throw new InvalidOperationException($"Discharging info in wrong session {hourlyInfo}");

                            break;
                        }

                    case (false, false, true): // Zero Net Home
                    case (false, false, false): // Disabled
                        {
                            if (sessions.InAnySession(hourlyInfo))
                                throw new InvalidOperationException($"Zero net home or disabled hour in a (dis)charging session {hourlyInfo}");
                        }

                        break;

                    default:
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
#endif

        private async Task EvaluateSessions(Sessions sessions, List<HourlyInfo> hourlyInfos)
        {
            do
            {
                await EvaluateChargingHoursAndProfitability(sessions, hourlyInfos);
            }
            while (ShrinkSessions(sessions));

            await EvaluateChargingHoursAndProfitability(sessions, hourlyInfos);
        }

        private async Task EvaluateChargingHoursAndProfitability(Sessions sessions, List<HourlyInfo> hourlyInfos)
        {
            await CalculateChargeLeftAndProfits(hourlyInfos);

            sessions.CalculateProfits(_timeZoneService!);

            DetermineMaxToCharge(sessions);
        }

        /// <summary>
        /// Calculate the estimated charge per hour starting from the current hour.
        /// </summary>
        public async Task CalculateChargeLeftAndProfits(List<HourlyInfo> hourlyInfos)
        {
            double stateOfCharge = await _batteryContainer.GetStateOfCharge();
            double totalCapacity = _batteryContainer.GetTotalCapacity();
            double charge = 0.0;
            double dayNeed = _settingsConfig.RequiredHomeEnergy;
            double hourNeed = dayNeed / 24;
            double chargingCapacity = _sessyBatteryConfig.TotalChargingCapacity;
            double dischargingCapacity = _sessyBatteryConfig.TotalDischargingCapacity;
            var now = _timeZoneService.Now;
            var localTimeHour = now.Date.AddHours(now.Hour);

            hourlyInfos.ForEach(hi => hi.ChargeLeft = 0.0);

            var hourlyInfoList = hourlyInfos
                // .Where(hp => hp.Time.Date.AddHours(hp.Time.Hour) >= localTimeHour)
                .OrderBy(hp => hp.Time)
                .ToList();
            List<HourlyInfo> lastChargingSession = new List<HourlyInfo>();

            HourlyInfo? previous = null;

            foreach (var hourlyInfo in hourlyInfoList)
            {
                if (previous != null)
                {
                    if (hourlyInfo.Time == now.Date.AddHours(now.Hour))
                    {
                        charge = stateOfCharge * totalCapacity;
                        previous.ChargeLeft = charge;
                        previous.ChargeLeftPercentage = charge / (totalCapacity / 100);
                    }

                    switch ((hourlyInfo.Charging, hourlyInfo.Discharging, hourlyInfo.ZeroNetHome))
                    {
                        case (true, false, false): // Charging
                            {
                                var session = sessions.GetSession(hourlyInfo);

                                charge = Math.Min(charge + chargingCapacity, session.MaxChargeNeeded);

                                if (lastChargingSession.Count > 0)
                                {
                                    var lastDateCharging = lastChargingSession.Max(hi => hi.Time);

                                    if ((hourlyInfo.Time - lastDateCharging).Hours > 2)
                                    {
                                        lastChargingSession.Clear();
                                    }
                                }

                                lastChargingSession.Add(hourlyInfo);
                                break;
                            }

                        case (false, true, false): // Discharging
                            {
                                charge = dischargingCapacity > charge ? 0.0 : charge - dischargingCapacity;
                                break;
                            }

                        case (false, false, true): // Zero net home
                            {
                                charge = hourNeed > charge ? 0.0 : charge - hourNeed;
                                break;
                            }

                        case (false, false, false): // Disabled
                            {
                                break;
                            }

                        default:
                            break;
                    }

                    charge += hourlyInfo.SolarPowerInWatts;
                }

                if (hourlyInfo.Time < now.Date.AddHours(now.Hour))
                {
                    hourlyInfo.ChargeLeft = 0.0;
                    hourlyInfo.ChargeLeftPercentage = 0.0;
                }
                else
                {
                    hourlyInfo.ChargeLeft = charge;
                    hourlyInfo.ChargeLeftPercentage = charge / (totalCapacity / 100);
                }

                previous = hourlyInfo;
            }
        }

        /// <summary>
        /// In this fase all session are created. Now it's time to evaluate which
        /// ones to keep. The following sessions are filtered out.
        /// - Charging sessions larger than the max charging hours
        /// - Discharging sessions that are not profitable.
        /// </summary>
        private void RemoveExtraChargingSessions(Sessions sessions)
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

                changed1 = CombineSessions(sessions);

                changed2 = RemoveExtraHours(sessions);

                changed3 = RemoveEmptySessions(sessions);

                changed4 = CheckProfitability(sessions);
            } while (changed1 || changed2 || changed3 || changed4);
        }

        /// <summary>
        /// Check if discharging sessions are profitable.
        /// </summary>
        private bool CheckProfitability(Sessions sessions)
        {
            var changed = false;

            var sessionList = sessions.SessionList.OrderBy(se => se.FirstDate).ToList();

            for (int currentSession = 0; currentSession < sessionList.Count; currentSession++)
            {
                switch (sessionList[currentSession].Mode)
                {
                    case Modes.Charging:
                        {
                            var dischargingSession = currentSession + 1;

                            if (dischargingSession < sessionList.Count)
                            {
                                if (sessionList[dischargingSession].Mode == Modes.Discharging)
                                {
                                    var chargingHours = sessionList[currentSession].GetHourlyInfoList().OrderBy(hp => hp.Price).ToList();
                                    var dischargingHours = sessionList[dischargingSession].GetHourlyInfoList().OrderBy(hp => hp.Price).ToList();

                                    var chargingEnumerator = chargingHours.GetEnumerator();
                                    var dischargingEnumerator = dischargingHours.GetEnumerator();

                                    var hasCharging = chargingEnumerator.MoveNext();
                                    var hasDischarging = dischargingEnumerator.MoveNext();

                                    while (hasCharging || hasDischarging)
                                    {
                                        if (hasCharging)
                                        {
                                            if (hasDischarging)
                                            {
                                                if (chargingEnumerator.Current.Price + _settingsConfig.CycleCost > dischargingEnumerator.Current.Price)
                                                {
                                                    var dcSession = sessions.SessionList[dischargingSession];
                                                    dcSession.RemoveHourlyInfo(dischargingEnumerator.Current);
                                                    hasDischarging = dischargingEnumerator.MoveNext();
                                                    changed = true;
                                                }
                                                else
                                                {
                                                    hasDischarging = dischargingEnumerator.MoveNext();
                                                    hasCharging = chargingEnumerator.MoveNext();
                                                }
                                            }
                                            else
                                                break;
                                        }
                                        else
                                        {
                                            if (hasDischarging)
                                            {
                                                sessions.SessionList[dischargingSession].RemoveHourlyInfo(dischargingEnumerator.Current);
                                                hasDischarging = dischargingEnumerator.MoveNext();
                                                changed = true;
                                            }
                                        }
                                    }
                                }
                                else
                                    _logger.LogInformation($"Charging session without discharging session {sessionList[currentSession]}");
                            }
                        }
                        break;

                    case Modes.Discharging:
                    default:
                        break;
                }
            }

            return changed;
        }

        /// <summary>
        /// Remove hours outside the max hours.
        /// </summary>
        private bool RemoveExtraHours(Sessions sessions)
        {
            var changed = false;

            List<HourlyInfo> listToLimit;

            foreach (var session in sessions.SessionList.ToList())
            {
                switch (session.Mode)
                {
                    case Modes.Charging:
                        {
                            listToLimit = session.GetHourlyInfoList().OrderByDescending(hp => hp.Price).ToList();

                            break;
                        }

                    case Modes.Discharging:
                        {
                            listToLimit = session.GetHourlyInfoList().OrderBy(hp => hp.Price).ToList();
                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Unknown mode {session.Mode}");
                }

                for (int i = session.MaxHours; i < listToLimit.Count; i++)
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
        private bool RemoveEmptySessions(Sessions sessions)
        {
            var changed = false;

            foreach (var session in sessions.SessionList.ToList())
            {
                if (session.GetHourlyInfoList().Count() == 0)
                {
                    sessions.RemoveSession(session);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Merge succeeding sessions of the same type.
        /// </summary>
        private bool CombineSessions(Sessions sessions)
        {
            var changed = false;

            Session? lastSession = null;
            var list = sessions.SessionList
                        .Where(se => se.GetHourlyInfoList().Count > 0)
                        .OrderBy(se => se.FirstDate).ToList();

            foreach (var session in list)
            {
                if (lastSession != null)
                {
                    if (lastSession.Mode == session.Mode)
                    {
                        var maxZeroNetHome = GetMaxZeroNetHomeHours(lastSession, session);
                        var hoursBetween = (session.FirstDate - lastSession.LastDate).Hours;

                        if (hoursBetween <= 3)
                        {
                            sessions.MergeSessions(lastSession, session);

                            sessions.RemoveSession(session);

                            changed = true;
                        }
                    }
                }

                lastSession = session;
            }

            return changed;
        }

        /// <summary>
        /// Shrink sessions if the hours needed to charge to 100% is less than calculated.
        /// </summary>
        private bool ShrinkSessions(Sessions sessions)
        {
            bool changed = false;

            changed = RemoveHoursAboveChargeNeeded(sessions);

            return changed;
        }

        /// <summary>
        /// This routine detects charge session that follow each other and determines how much
        /// charge is needed for the session.
        /// </summary>
        private bool DetermineMaxToCharge(Sessions sessions)
        {
            var changed = false;
            Session? previousSession = null;
            double maxChargeNeeded = 0.0;
            var totalCapacity = _batteryContainer.GetTotalCapacity();

            sessions.SessionList.ToList().ForEach(hi => hi.MaxChargeNeeded = totalCapacity);

            foreach (var session in sessions.SessionList.OrderBy(se => se.FirstDate))
            {
                if (previousSession != null)
                {
                    if (session.Mode == Modes.Charging &&
                        previousSession.Mode == Modes.Charging)
                    {
                        if (previousSession.IsCheaper(session))
                        {
                            maxChargeNeeded = totalCapacity;
                        }
                        else
                        {
                            var infoObjectsBetween = hourlyInfos!
                                .Where(hi => hi.Time < session.FirstDate && hi.Time > previousSession.LastDate && hi.ZeroNetHome)
                                .ToList();

                            if (infoObjectsBetween.Any())
                            {
                                // TODO: Determine needs from historic data.
                                maxChargeNeeded = (_settingsConfig.RequiredHomeEnergy / 24) * infoObjectsBetween.Count();
                                maxChargeNeeded *= 1.3; // We take a little more to be sure that we have enough.

                                previousSession.MaxChargeNeeded = maxChargeNeeded;

                                changed = true;
                            }
                        }
                    }
                }

                previousSession = session;
            }

            return changed;
        }

        private static bool RemoveHoursAboveChargeNeeded(Sessions sessions)
        {
            var changed = false;

            foreach (var session in sessions.SessionList)
            {
                var maxHours = session.GetChargingHours();

                if (session.RemoveAllAfter(maxHours))
                {
                    changed = true;
                }
            }

            return changed;
        }

        private double GetMaxZeroNetHomeHours(Session lastSession, Session session)
        {
            var homeNeeds = _settingsConfig.RequiredHomeEnergy;

            if (lastSession.Mode == Modes.Charging && session.Mode == Modes.Charging)
            {
                var timeSpan = session.FirstDate - lastSession.LastDate;

                return timeSpan.Hours;
            }

            return 24.0;
        }

        /// <summary>
        /// Determine when the prices are the highest en the lowest.
        /// </summary>
        private Sessions CreateSessions(List<HourlyInfo> hourlyInfos)
        {
            var averagePrice = hourlyInfos.Average(hp => hp.Price);

            double totalBatteryCapacity = _batteryContainer.GetTotalCapacity();
            double chargingPower = _batteryContainer.GetChargingCapacity();
            double dischargingPower = _batteryContainer.GetDischargingCapacity();
            int maxChargingHours = (int)Math.Ceiling(totalBatteryCapacity / chargingPower);
            int maxDischargingHours = (int)Math.Ceiling(totalBatteryCapacity / dischargingPower);
            var homeNeeds = _settingsConfig.RequiredHomeEnergy;

            var loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

            Sessions sessions = new Sessions(hourlyInfos,
                                             _settingsConfig,
                                             _batteryContainer,
                                             loggerFactory);
            if (hourlyInfos != null && hourlyInfos.Count > 0)
            {
                // Check the first element
                if (hourlyInfos.Count > 1)
                {
                    if (hourlyInfos[0].SmoothedPrice < hourlyInfos[1].SmoothedPrice)
                    {
                        sessions.AddNewSession(Modes.Charging, hourlyInfos[0], averagePrice);
                    }

                    if (hourlyInfos[0].SmoothedPrice > hourlyInfos[1].SmoothedPrice)
                    {
                        sessions.AddNewSession(Modes.Discharging, hourlyInfos[0], averagePrice);
                    }
                }

                // Check the elements in between.
                for (var i = 1; i < hourlyInfos.Count - 1; i++)
                {
                    if (hourlyInfos[i].SmoothedPrice < hourlyInfos[i - 1].SmoothedPrice && hourlyInfos[i].SmoothedPrice < hourlyInfos[i + 1].SmoothedPrice)
                    {
                        sessions.AddNewSession(Modes.Charging, hourlyInfos[i], averagePrice);
                    }

                    if (hourlyInfos[i].SmoothedPrice > hourlyInfos[i - 1].SmoothedPrice && hourlyInfos[i].SmoothedPrice > hourlyInfos[i + 1].SmoothedPrice)
                    {
                        sessions.AddNewSession(Modes.Discharging, hourlyInfos[i], averagePrice);
                    }
                }

                // Check the last element
                if (hourlyInfos.Count > 1)
                {
                    if (hourlyInfos[hourlyInfos.Count - 1].SmoothedPrice < hourlyInfos[hourlyInfos.Count - 2].SmoothedPrice)
                        sessions.AddNewSession(Modes.Charging, hourlyInfos[hourlyInfos.Count - 1], averagePrice);

                    if (hourlyInfos[hourlyInfos.Count - 1].SmoothedPrice > hourlyInfos[hourlyInfos.Count - 2].SmoothedPrice)
                        sessions.AddNewSession(Modes.Discharging, hourlyInfos[hourlyInfos.Count - 1], averagePrice);
                }
            }
            else
                _logger.LogWarning("HourlyInfos is empty!!");

            return sessions;
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                hourlyInfos.Clear();
                hourlyInfos = null;
                _sessyService = null;
                _p1MeterService = null;
                _dayAheadMarketService = null;
                _solarEdgeService = null;
                _solarService = null;
                _batteryContainer = null;
                _timeZoneService = null;

                base.Dispose();

                _isDisposed = true;
            }
        }
    }

}
