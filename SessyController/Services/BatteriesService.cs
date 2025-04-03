﻿using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Services.Items;
using System.ComponentModel;
using static SessyController.Services.Items.Session;

namespace SessyController.Services
{
    /// <summary>
    /// This service maintains all batteries in the system.
    /// </summary>
    public partial class BatteriesService : SessyBackgroundService
    {
        private IServiceScope _scope { get; set; }
        private SessyService? _sessyService { get; set; }
        private P1MeterService? _p1MeterService { get; set; }
        private DayAheadMarketService? _dayAheadMarketService { get; set; }
        private SolarEdgeService? _solarEdgeService { get; set; }
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

        private LoggingService<BatteriesService> _logger { get; set; }

        private static List<HourlyInfo>? hourlyInfos { get; set; } = new List<HourlyInfo>();

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
            _solarEdgeService = _scope.ServiceProvider.GetRequiredService<SolarEdgeService>();
            _solarService = _scope.ServiceProvider.GetRequiredService<SolarService>();
            _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _powerEstimatesService = _scope.ServiceProvider.GetRequiredService<PowerEstimatesService>();
            _weatherService = _scope.ServiceProvider.GetRequiredService<WeatherService>();
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
                    GC.Collect(); // TODO: Move it's own background service.

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

            _logger.LogInformation("BatteriesService stopped.");
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
                    await DetermineChargingHours();

                    if (_sessions != null)
                    {
                        _solarService.GetExpectedSolarPower(hourlyInfos!);

                        await EvaluateSessions();

                        HourlyInfo? currentHourlyInfo = GetCurrentHourlyInfo();

                        if (!(_dayAheadMarketService.PricesAvailable && currentHourlyInfo != null))
                        {
                            _logger.LogInformation("No prices available from ENTSO-E, switching to manual charging");

                            HandleManualCharging();
                        }
                        else
                        {
                            await HandleAutomaticCharging(_sessions);
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

        public Sessions? GetSessions()
        {
            return _sessions;
        }

        private async Task HandleAutomaticCharging(Sessions sessions)
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
            HourlyInfo? currentHourlyInfo = GetCurrentHourlyInfo();

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

        private class SessionInfo
        {
            public SessionInfo(Session? session)
            {
                FirstDate = session.FirstDate;
                LastDate = session.LastDate;
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
        private async Task CancelSessionIfStateRequiresIt(Sessions sessions, HourlyInfo currentHourlyInfo)
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
                    bool batteriesAreFull = await AreAllBatteriesFull();
                    bool chargeIsEnough = await IsMaxChargeNeededReached(currentHourlyInfo);

                    if (batteriesAreFull || chargeIsEnough)
                    {
                        StopSession(session);
                        _logger.LogWarning($"Warning: Charging session stopped because batteries are full (enough). {session}, batteries are full: {batteriesAreFull}, charge at maximum: {chargeIsEnough}");
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
        /// Check whether the current hourly info is in a discharging session and return
        /// true if so and the calculated min charge needed is reached.
        /// </summary>
        private async Task<bool> IsMinChargeNeededReached(HourlyInfo currentHourlyInfo)
        {
            var currentChargeState = await _batteryContainer.GetStateOfChargeInWatts();

            return (currentHourlyInfo.Discharging && currentChargeState <= currentHourlyInfo.ChargeNeeded);
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
        public async Task DetermineChargingHours()
        {
            DateTime localTime = _timeZoneService.Now;

            if (FetchPricesFromENTSO_E(localTime))
            {
                await GetChargingHours();
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

            HourlyInfo.AddSmoothedPrices(hourlyInfos, 3);

            return hourlyInfos != null && hourlyInfos.Count > 0;
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
                DateTime now = _timeZoneService.Now;
                DateTime nowHour = now.Date.AddHours(now.Hour);

                hourlyInfos = hourlyInfos!
                    .OrderBy(hp => hp.Time)
                    .ToList();

                CreateSessions();

                if (_sessions != null)
                {
                    await RemoveExtraChargingSessions();

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
                        {
                            if (_sessions.InAnySession(hourlyInfo))
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
            _sessions.CalculateProfits(_timeZoneService!);

            CalculateChargeNeeded();

            await CalculateChargeLeft(hourlyInfos!);
        }

        /// <summary>
        /// Calculate the estimated charge per hour starting from the current hour.
        /// </summary>
        public async Task CalculateChargeLeft(List<HourlyInfo> hourlyInfos)
        {
            double charge = 0.0;
            double totalCapacity = _batteryContainer.GetTotalCapacity();
            double dayNeed = _settingsConfig.RequiredHomeEnergy;
            double hourNeed = dayNeed / 24;
            double chargingCapacity = _sessyBatteryConfig.TotalChargingCapacity;
            double dischargingCapacity = _sessyBatteryConfig.TotalDischargingCapacity;
            var now = _timeZoneService.Now;
            var localTimeHour = now.Date.AddHours(now.Hour);
            charge = await _batteryContainer.GetStateOfChargeInWatts();

            var hourlyInfoList = hourlyInfos
                .OrderBy(hp => hp.Time)
                .ToList();

            hourlyInfoList.ForEach(hi => hi.ChargeLeft = charge);

            foreach (var hourlyInfo in hourlyInfoList.Where(hi => hi.Time >= now.DateHour()))
            {
                var session = _sessions.GetSession(hourlyInfo);

                switch (hourlyInfo.Mode)
                {
                    case Modes.Charging:
                        charge += _batteryContainer.GetChargingCapacity();
                        charge += hourlyInfo.SolarPowerInWatts;
                        charge = Math.Min(charge, hourlyInfo.ChargeNeeded);
                        break;

                    case Modes.Discharging:
                        charge -= _batteryContainer.GetDischargingCapacity();
                        charge += hourlyInfo.SolarPowerInWatts;
                        charge = Math.Max(charge, hourlyInfo.ChargeNeeded);
                        break;

                    case Modes.ZeroNetHome:
                        charge -= hourNeed;
                        charge += hourlyInfo.SolarPowerInWatts;
                        break;

                    case Modes.Disabled:
                        break;

                    case Modes.Unknown:
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
        private async Task RemoveExtraChargingSessions()
        {
            bool changed1;
            bool changed2;
            bool changed3;
            bool changed4;
            bool changed5;
            bool changed6;
            bool changed7;

            do
            {
                changed1 = false;
                changed2 = false;
                changed3 = false;
                changed4 = false;
                changed5 = false;
                changed6 = false;
                changed7 = false;

                changed1 = RemoveMoreExpensiveChargingSessions();

                changed2 = RemoveEmptySessions();

                changed3 = RemoveExtraHours();

                changed4 = RemoveEmptySessions();

                changed5 = CheckProfitability();

                changed6 = RemoveEmptySessions();

                // changed7 = await RemoveSessionIfMaxChargeIsReached();
            } while (changed1 || changed2 || changed3 || changed4 || changed5 || changed6 || changed7);
        }

        /// <summary>
        /// Remove the current session if the charge is reached.
        /// </summary>
        private async Task<bool> RemoveSessionIfMaxChargeIsReached()
        {
            var changed = false;
            var now = _timeZoneService.Now;

            var currentHourlyInfo = hourlyInfos!.FirstOrDefault(hi => hi.Time.DateHour() == now.DateHour());

            if (currentHourlyInfo != null)
            {
                if (await IsMaxChargeNeededReached(currentHourlyInfo))
                {
                    Session? session = _sessions.GetSession(currentHourlyInfo);

                    if (session == null) throw new InvalidOperationException($"Session not found for {currentHourlyInfo}");

                    _sessions.RemoveSession(session);

                    changed = true;
                }
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

            foreach (var session in _sessions.SessionList.OrderBy(se => se.FirstDate))
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
                            if (chargeEnumerator.Current.BuyingPrice + _settingsConfig.CycleCost > dischargeEnumerator.Current.BuyingPrice)
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
        /// Remove hours outside the max hours.
        /// </summary>
        private bool RemoveExtraHours()
        {
            var changed = false;

            List<HourlyInfo> listToLimit;

            foreach (var session in _sessions.SessionList.ToList())
            {
                switch (session.Mode)
                {
                    case Modes.Charging:
                        {
                            listToLimit = session.GetHourlyInfoList().OrderByDescending(hp => hp.BuyingPrice).ToList();

                            break;
                        }

                    case Modes.Discharging:
                        {
                            listToLimit = session.GetHourlyInfoList().OrderBy(hp => hp.BuyingPrice).ToList();
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
        /// Merge succeeding sessions of the same type.
        /// </summary>
        private bool RemoveMoreExpensiveChargingSessions()
        {
            var changed = false;

            Session? previousSession = null;

            var list = _sessions.SessionList
                        .Where(se => se.GetHourlyInfoList().Count > 0)
                        .OrderBy(se => se.FirstDate)
                        .ToList();

            foreach (var session in list)
            {
                if (previousSession != null)
                {
                    if (previousSession.Mode == session.Mode)
                    {
                        var hoursBetween = (session.FirstDate - previousSession.LastDate).Hours - 1;

                        if (session.Mode == Modes.Charging)
                        {
                            var maxZeroNetHomeHours = _sessions.GetMaxZeroNetHomeHours(previousSession, session);

                            if (hoursBetween <= maxZeroNetHomeHours)
                            {
                                if (session.IsCheaper(previousSession))
                                    _sessions.RemoveSession(previousSession);
                                else
                                    _sessions.RemoveSession(session);

                                changed = true;
                            }
                        }
                        else if (session.Mode == Modes.Discharging)
                        {
                            if (hoursBetween <= 3)
                            {
                                if (session.IsCheaper(previousSession))
                                    _sessions.RemoveSession(session);
                                else
                                    _sessions.RemoveSession(previousSession);

                                changed = true;
                            }
                        }
                    }
                }

                previousSession = session;
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
                var maxHours = session.GetChargingHours();

                if (session.RemoveAllAfter(maxHours))
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

            foreach (var nextSession in _sessions.SessionList.OrderBy(se => se.FirstDate))
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
                            break;
                    }

                    HandleHoursBetweenSessions(previousSession, nextSession);
                }

                previousSession = nextSession;
            }
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
                    break;
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
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// The previous session is a discharging session. The next is a charging session. Handle it.
        /// </summary>
        private void HandleDischargingChargingCalculation(Session previousSession, Session nextSession)
        {
            List<HourlyInfo> infoObjectsBetween = GetInfoObjectsBetween(previousSession, nextSession);
            var totalNeed = GetEstimatePowerNeeded(infoObjectsBetween);

            previousSession.SetChargeNeeded(totalNeed);
        }

        /// <summary>
        /// The previous session is a charging session. The next a discharging session. Handle it.
        /// </summary>
        private void HandleChargingDischargingSessions(Session previousSession, Session nextSession)
        {
            List<HourlyInfo> infoObjectsBetween = GetInfoObjectsBetween(previousSession, nextSession);
            var estimateNeeded = GetEstimatePowerNeeded(infoObjectsBetween);

            var chargeCalculated = estimateNeeded + nextSession.GetChargeNeeded();
            var chargeNeeded = Math.Min(chargeCalculated, _batteryContainer.GetTotalCapacity());

            previousSession.SetChargeNeeded(chargeNeeded);
        }

        /// <summary>
        /// Both sessions are charging sessions. Determine the charge needed for the previous session.
        /// </summary>
        private void HandleChargingChargingSessions(Session previousSession, Session nextSession)
        {
            var totalCapacity = _batteryContainer.GetTotalCapacity();
            List<HourlyInfo> infoObjectsBetween = GetInfoObjectsBetween(previousSession, nextSession);

            if (previousSession.IsCheaper(nextSession))
            {
                previousSession.SetChargeNeeded(totalCapacity);
                infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = totalCapacity);
            }
            else
            {
                if (infoObjectsBetween.Any())
                {
                    var count = infoObjectsBetween.Where(hi => hi.Mode == Modes.ZeroNetHome).Count();

                    var chargeNeeded = GetEstimatePowerNeeded(infoObjectsBetween);

                    chargeNeeded += nextSession.GetChargeNeeded();

                    infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = chargeNeeded);
                    previousSession.SetChargeNeeded(chargeNeeded);
                }
                else
                {
                    previousSession.SetChargeNeeded(0.0);
                }
            }
        }

        /// <summary>
        /// Handle the hourly info objects between 2 sessions.
        /// </summary>
        private void HandleHoursBetweenSessions(Session previousSession, Session nextSession)
        {
            List<HourlyInfo> infoObjectsBetween = GetInfoObjectsBetween(previousSession, nextSession);
            var count = infoObjectsBetween.Where(hi => hi.ZeroNetHome).Count();
            var hourNeed = (_settingsConfig.RequiredHomeEnergy / 24) * count;

            infoObjectsBetween.ForEach(hi => hi.ChargeNeeded = hourNeed);
        }

        private double GetEstimatePowerNeeded(List<HourlyInfo> infoObjectsBetween)
        {
            double power = 0.0;

            foreach (var hourlyInfo in infoObjectsBetween)
            {
                // TODO: The estimate is incorrect. It calculates the net power from the grid (Zero Net Home), not the needs of the home.
                //var temperature = _weatherService.GetTemperature(hourlyInfo.Time);

                //if (temperature != null)
                //{
                //    power += _powerEstimatesService.GetPowerEstimate(hourlyInfo.Time, temperature.Value);
                //}
                //else
                //{
                // power += _settingsConfig.RequiredHomeEnergy / 24.0;
                //}

                power += _settingsConfig.RequiredHomeEnergy / 24.0;
            }

            return power;
        }

        /// <summary>
        /// Gets the hourlyInfo objects between 2 sessions.
        /// </summary>
        private static List<HourlyInfo> GetInfoObjectsBetween(Session previousSession, Session session)
        {
            return hourlyInfos!
                .Where(hi => hi.Time < session.FirstDate && hi.Time > previousSession.LastDate)
                .ToList();
        }

        /// <summary>
        /// Determine when the prices are the highest en the lowest.
        /// </summary>
        private void CreateSessions()
        {
            _sessions?.Dispose();

            _sessions = null;

            if (hourlyInfos != null && hourlyInfos.Count() > 0)
            {
                double totalBatteryCapacity = _batteryContainer.GetTotalCapacity();
                double chargingPower = _batteryContainer.GetChargingCapacity();
                double dischargingPower = _batteryContainer.GetDischargingCapacity();
                int maxChargingHours = (int)Math.Ceiling(totalBatteryCapacity / chargingPower);
                int maxDischargingHours = (int)Math.Ceiling(totalBatteryCapacity / dischargingPower);
                var homeNeeds = _settingsConfig.RequiredHomeEnergy;

                var loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

                _sessions = new Sessions(hourlyInfos,
                                         _settingsConfig,
                                         _batteryContainer,
                                         _timeZoneService,
                                         loggerFactory);

                // Check the first element
                if (hourlyInfos.Count > 1)
                {
                    if (hourlyInfos[0].BuyingPrice <= hourlyInfos[1].BuyingPrice)
                    {
                        if (!_sessions.InAnySession(hourlyInfos[0]))
                            _sessions.AddNewSession(Modes.Charging, hourlyInfos[0]);
                    }

                    if (hourlyInfos[0].BuyingPrice > hourlyInfos[1].BuyingPrice)
                    {
                        if (!_sessions.InAnySession(hourlyInfos[0]))
                            _sessions.AddNewSession(Modes.Discharging, hourlyInfos[0]);
                    }
                }

                // Check the elements in between.
                for (var index = 1; index < hourlyInfos.Count - 2; index++)
                {
                    if (hourlyInfos[index].BuyingPrice < hourlyInfos[index - 1].BuyingPrice && hourlyInfos[index].BuyingPrice <= hourlyInfos[index + 1].BuyingPrice)
                    {
                        if (!_sessions.InAnySession(hourlyInfos[index]))
                            _sessions.AddNewSession(Modes.Charging, hourlyInfos[index]);
                    }

                    if (hourlyInfos[index].BuyingPrice > hourlyInfos[index - 1].BuyingPrice && hourlyInfos[index].BuyingPrice >= hourlyInfos[index + 1].BuyingPrice)
                    {
                        if (!_sessions.InAnySession(hourlyInfos[index]))
                            _sessions.AddNewSession(Modes.Discharging, hourlyInfos[index]);
                    }
                }

                // Check the last element
                if (hourlyInfos.Count >= 1)
                {
                    var index = hourlyInfos.Count - 1;

                    if (hourlyInfos[index].BuyingPrice < hourlyInfos[index - 1].BuyingPrice)
                    {
                        if (!_sessions.InAnySession(hourlyInfos[index]))
                            _sessions.AddNewSession(Modes.Charging, hourlyInfos[index]);
                    }

                    if (hourlyInfos[index].BuyingPrice > hourlyInfos[index - 1].BuyingPrice)
                    {
                        if (!_sessions.InAnySession(hourlyInfos[index]))
                            _sessions.AddNewSession(Modes.Discharging, hourlyInfos[index]);
                    }
                }
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
                _solarEdgeService = null;
                _solarService = null;
                _batteryContainer = null;
                _timeZoneService = null;

                _scope.Dispose();

                base.Dispose();

                _isDisposed = true;
            }
        }
    }

}
