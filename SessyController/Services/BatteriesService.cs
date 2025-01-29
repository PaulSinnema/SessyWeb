using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;
using System.Collections.Generic;
using static SessyController.Services.Items.Session;
using static SessyController.Services.Items.Sessions;

namespace SessyController.Services
{
    /// <summary>
    /// This service maintains all batteries in the system.
    /// </summary>
    public partial class BatteriesService : BackgroundService
    {
        private SessyService _sessyService;
        private P1MeterService _p1MeterService;
        private DayAheadMarketService _dayAheadMarketService;
        private SolarEdgeService _solarEdgeService;
        private readonly SolarService _solarService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly SettingsConfig? _settingsConfig;
        private readonly SessyBatteryConfig _sessyBatteryConfig;
        private readonly BatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;
        private readonly LoggingService<BatteriesService> _logger;
        private static List<HourlyInfo> hourlyInfos { get; set; } = new List<HourlyInfo>();

        public BatteriesService(LoggingService<BatteriesService> logger,
                                IOptions<SettingsConfig> settingsConfig,
                                IOptions<SessyBatteryConfig> sessyBatteryConfig,
                                IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;

            _logger.LogInformation("BatteriesService starting");

            _serviceScopeFactory = serviceScopeFactory;

            _logger.LogInformation("BatteriesService checking settings");

            _settingsConfig = settingsConfig.Value;
            _sessyBatteryConfig = sessyBatteryConfig.Value;

            if (_settingsConfig == null) throw new InvalidOperationException("ManagementSettings missing");
            if (_sessyBatteryConfig == null) throw new InvalidOperationException("Sessy:Batteries missing");

            using (var scope = _serviceScopeFactory.CreateScope())
            {
                _sessyService = scope.ServiceProvider.GetRequiredService<SessyService>();
                _p1MeterService = scope.ServiceProvider.GetRequiredService<P1MeterService>();
                _dayAheadMarketService = scope.ServiceProvider.GetRequiredService<DayAheadMarketService>();
                _solarEdgeService = scope.ServiceProvider.GetRequiredService<SolarEdgeService>();
                _solarService = scope.ServiceProvider.GetRequiredService<SolarService>();
                _batteryContainer = scope.ServiceProvider.GetRequiredService<BatteryContainer>();
                _timeZoneService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            }
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
                    _logger.LogException(ex, "An error occurred while managing batteries.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancelationToken);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay
                }
            }

            _logger.LogInformation("BatteriesService stopped.");
        }

        /// <summary>
        /// This routine is called periodicly as a background task.
        /// </summary>
        public async Task Process(CancellationToken cancellationToken)
        {
            if (_dayAheadMarketService.PricesInitialized)
            {
                await DetermineChargingHours();

                HourlyInfo? currentHourlyInfo = GetCurrentHourlyInfo();

#if !DEBUG
                if (!(_dayAheadMarketService.PricesAvailable && currentHourlyInfo != null))
                {
                    _logger.LogWarning("No prices available from ENTSO-E, switching to manual charging");

                    HandleManualCharging();
                }
                else
                {
                    await HandleAutomaticCharging();
                }
#endif
            }
        }

        /// <summary>
        /// Returns the fetche and analyzed hourly prices.
        /// </summary>
        public List<HourlyInfo>? GetHourlyInfos()
        {
            return hourlyInfos;
        }

        private async Task HandleAutomaticCharging()
        {
            HourlyInfo? currentHourlyInfo = GetCurrentHourlyInfo();

            if (currentHourlyInfo != null)
            {
                await CancelSessionIfStateRequiresIt(currentHourlyInfo);

                if (currentHourlyInfo.Charging)
                    _batteryContainer.StartCharging();
                else if (currentHourlyInfo.Discharging)
                    _batteryContainer.StartDisharging();
                else if (currentHourlyInfo.ZeroNetHome)
                    _batteryContainer.StartNetZeroHome();
                else if (currentHourlyInfo.CapacityExhausted)
                    _batteryContainer.StopAll();
            }
        }

        private void HandleManualCharging()
        {
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                _batteryContainer.StartCharging();
            else if (_settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                _batteryContainer.StartDisharging();
            else if (_settingsConfig.ManualNetZeroHomeHours.Contains(localTime.Hour))
                _batteryContainer.StartNetZeroHome();
            else
                _batteryContainer.StopAll();
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
        private async Task CancelSessionIfStateRequiresIt(HourlyInfo currentHourlyInfo)
        {
            if (currentHourlyInfo.Charging)
            {
                bool batteriesAreFull = await AreAllBattiesFull(currentHourlyInfo);

                if (batteriesAreFull)
                    StopChargingSession(currentHourlyInfo);
            }
            else if (currentHourlyInfo.Discharging)
            {
                bool batteriesAreEmpty = await AreAllBattiesEmpty(currentHourlyInfo);

                if (batteriesAreEmpty)
                    StopDischargingSession(currentHourlyInfo);
            }
        }

        /// <summary>
        /// Cancel charging for current and future consecutive charging hours.
        /// </summary>
        private static void StopChargingSession(HourlyInfo currentHourlyInfo)
        {
            if (hourlyInfos != null)
            {
                var enumPrices = hourlyInfos.GetEnumerator();

                while (enumPrices.Current.Time.Hour < currentHourlyInfo.Time.Hour)
                    if (!enumPrices.MoveNext())
                        return;

                while (enumPrices.Current.Charging)
                {
                    enumPrices.Current.Charging = false; // Stop charging

                    if (!enumPrices.MoveNext())
                        return;
                }
            }
        }

        /// <summary>
        /// Cancel discharging for current and future consequtive discharging hours.
        /// </summary>
        private static void StopDischargingSession(HourlyInfo currentHourlyInfo)
        {
            if (hourlyInfos != null)
            {
                var enumPrices = hourlyInfos.GetEnumerator();

                while (enumPrices.Current.Time.Hour < currentHourlyInfo.Time.Hour)
                    if (!enumPrices.MoveNext())
                        return;

                while (enumPrices.Current.Discharging)
                {
                    enumPrices.Current.Discharging = false; // Stop charging

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
        /// Get the total state of charge. 1 = 100%
        /// </summary>
        private async Task<double> GetTotalStateOfCharge()
        {
            double stateOfCharge = 0.0;
            double count = 0;

            foreach (var battery in _batteryContainer.Batteries.ToList())
            {
                PowerStatus? powerStatus = await battery.GetPowerStatus();

                stateOfCharge += powerStatus.Sessy.StateOfCharge;
                count++;
            }

            if (count > 0)
                return stateOfCharge / count;

            return 0.0;
        }

        private bool hasCheckedLastDate1600 = false;
        private DateTime lastDateChecked = DateTime.MinValue;

        /// <summary>
        /// Determine when to charge the batteries.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> DetermineChargingHours()
        {
            DateTime localTime = _timeZoneService.Now;

            if (localTime.Date > lastDateChecked.Date)
                hasCheckedLastDate1600 = false;

            if (!hasCheckedLastDate1600 && localTime.Hour >= 16)
                hasCheckedLastDate1600 = false;

            var tomorrow = localTime.AddDays(1);

            var hoursArePresent = localTime.Hour >= 23 &&
                hourlyInfos?
                .Where(hp => hp.Time == tomorrow.Date)
                .Count() > 0;

            if (hasCheckedLastDate1600 && !hoursArePresent)
                hasCheckedLastDate1600 = false;

            if (!hasCheckedLastDate1600)
            {
                if (!FetchPricesFromENTSO_E(localTime))
                    return false;

                await GetChargingHours();
                hasCheckedLastDate1600 = true;
                lastDateChecked = localTime;
            }

            return true;
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        /// <param name="localTime"></param>
        private bool FetchPricesFromENTSO_E(DateTime localTime)
        {
            // Get the available hourly prices from now.
            hourlyInfos = _dayAheadMarketService.GetPrices()
                .OrderBy(hp => hp.Time)
                .ToList();

            return hourlyInfos != null && hourlyInfos.Count > 0;
        }

        /// <summary>
        /// In this routine is determined when to charge the batteries.
        /// </summary>
        private async Task GetChargingHours()
        {
            hourlyInfos = hourlyInfos.OrderBy(hp => hp.Time).ToList();

            _solarService.GetExpectedSolarPower(hourlyInfos);

            List<int> lowestPrices = new List<int>();
            List<int> highestPrices = new List<int>();

            var averagePrice = hourlyInfos.Average(hp => hp.Price);

            Sessions sessions = CreateSessions(hourlyInfos, averagePrice);

            EvaluateSessionsOld(averagePrice, sessions);

            SetChargingMethods(sessions);

            await EvaluateSessions(sessions, hourlyInfos);
        }

        private static void SetChargingMethods(Sessions sessions)
        {
            foreach (var session in sessions.SessionList.ToList())
            {
                foreach (var hourlyInfo in session.PriceList)
                {
                    hourlyInfo.Charging = session.Mode == Modes.Charging;
                    hourlyInfo.Discharging = session.Mode == Modes.Discharging;
                }
            }
        }

        private async Task EvaluateSessions(Sessions sessions, List<HourlyInfo> hourlyInfos)
        {
            await CalculateChargeLeft(hourlyInfos);

            sessions.CalculateProfits();
        }

        /// <summary>
        /// Calculate the estimated charge per hour starting from the current hour.
        /// </summary>
        public async Task CalculateChargeLeft(List<HourlyInfo> hourlyInfos)
        {
            double stateOfCharge = await _batteryContainer.GetStateOfCharge();
            double totalCapacity = _batteryContainer.GetTotalCapacity();
            double charge =  stateOfCharge * totalCapacity;
            double dayNeed = _settingsConfig.RequiredHomeEnergy;
            double hourNeed = dayNeed / 24;
            double chargingCapacity = _sessyBatteryConfig.TotalChargingCapacity;
            double dischargingCapacity = _sessyBatteryConfig.TotalDischargingCapacity;
            var localTime = _timeZoneService.Now;

            foreach (var hourlyInfo in hourlyInfos
                .Where(hp => hp.Time >= localTime)
                .OrderBy(hp => hp.Time)
                .ToList())
            {
                if (hourlyInfo.Charging)
                {
                    charge = charge + chargingCapacity < totalCapacity ? charge + chargingCapacity : totalCapacity; 
                }
                else if (hourlyInfo.Discharging)
                {
                    charge = charge > dischargingCapacity ? charge - dischargingCapacity : 0.0;
                }
                else if (hourlyInfo.ZeroNetHome)
                {
                    charge = charge > hourNeed ? charge - hourNeed : 0.0;
                }

                hourlyInfo.ChargeLeft = charge;
            }
        }

        /// <summary>
        /// In this fase all session are created. Now it's time to evaluate which
        /// ones to keep. The following sessions are filtered out.
        /// - Charging sessions without a discharging session.
        /// - Charging sessions larger than the max charging hours
        /// - Discharging sessions that are not profitable.
        /// </summary>
        private void EvaluateSessionsOld(double averagePrice, Sessions sessions)
        {
            var changed1 = false;
            var changed2 = false;
            var changed3 = false;
            var changed4 = false;

            do
            {
                changed1 = false;
                changed2 = false;
                changed3 = false;
                changed4 = false;

                changed1 = MergeSessions(sessions, averagePrice);

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

            var sessionList = sessions.SessionList.OrderBy(se => se.First).ToList();

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
                                    var chargingHours = sessionList[currentSession].PriceList.OrderBy(hp => hp.Price).ToList();
                                    var dischargingHours = sessionList[dischargingSession].PriceList.OrderBy(hp => hp.Price).ToList();

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
                                                    dcSession.PriceList.Remove(dischargingEnumerator.Current);
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
                                                sessions.SessionList[dischargingSession].PriceList.Remove(dischargingEnumerator.Current);
                                                hasDischarging = dischargingEnumerator.MoveNext();
                                                changed = true;
                                            }
                                        }
                                    }
                                }
                                else
                                    _logger.LogWarning($"Charging session with discharging session {sessionList[currentSession]}");
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

            List<HourlyInfo> listToRemove;

            foreach (var session in sessions.SessionList.ToList())
            {
                switch (session.Mode)
                {
                    case Modes.Charging:
                        {
                            listToRemove = session.PriceList.OrderBy(hp => hp.Price).ToList();

                            break;
                        }

                    case Modes.Discharging:
                        {
                            listToRemove = session.PriceList.OrderByDescending(hp => hp.Price).ToList();
                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Unknown mode {session.Mode}");
                }

                for (int i = session.MaxHours; i < listToRemove.Count; i++)
                {
                    session.PriceList.Remove(listToRemove[i]);
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
                if (session.PriceList.Count() == 0)
                {
                    var index = sessions.SessionList.Remove(session);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Merge succeeding sessions of the same type.
        /// </summary>
        private bool MergeSessions(Sessions sessions, double averagePrice)
        {
            var changed = false;

            Session? lastSession = null;
            var list = sessions.SessionList
                        .Where(se => se.PriceList.Count > 0)
                        .OrderBy(se => se.First).ToList();

            foreach (var session in list)
            {
                if (lastSession != null)
                {

                    if (lastSession.Mode == session.Mode)
                    {
                        var maxZeroNetHome = GetMaxZeroNetHomeHours(lastSession, session);
                        var hoursBetween = (session.First - lastSession.Last).Hours;

                        if (hoursBetween <= 1)
                        {
                            foreach (var hourlyInfo in session.PriceList)
                            {
                                lastSession.PriceList.Add(hourlyInfo);
                            }

                            session.PriceList.Clear();
                            changed = true;
                        }
                    }
                }

                lastSession = session;
            }

            return changed;
        }

        private double GetMaxZeroNetHomeHours(Session lastSession, Session session)
        {
            var homeNeeds = _settingsConfig.RequiredHomeEnergy;

            if(lastSession.Mode == Modes.Charging && session.Mode == Modes.Charging)
            {
                var timeSpan = session.First - lastSession.Last;

                return timeSpan.Hours;
            }

            return 24.0;
        }

        /// <summary>
        /// Determine when the prices are the highest en the lowest.
        /// </summary>
        private Sessions CreateSessions(List<HourlyInfo> hourlyInfos, double averagePrice)
        {
            double totalBatteryCapacity = _batteryContainer.GetTotalCapacity();
            double chargingPower = _batteryContainer.GetChargingCapacity();
            double dischargingPower = _batteryContainer.GetDischargingCapacity();
            int maxChargingHours = (int)Math.Ceiling(totalBatteryCapacity / chargingPower);
            int maxDischargingHours = (int)Math.Ceiling(totalBatteryCapacity / dischargingPower);
            var homeNeeds = _settingsConfig.RequiredHomeEnergy;

            Sessions sessions = new Sessions(hourlyInfos, 
                                             maxChargingHours,
                                             maxDischargingHours,
                                             _sessyBatteryConfig.TotalChargingCapacity,
                                             _sessyBatteryConfig.TotalDischargingCapacity,
                                             totalBatteryCapacity,
                                             homeNeeds,
                                             _settingsConfig.CycleCost);

            if (hourlyInfos != null && hourlyInfos.Count > 0)
            {
                // Check the first element
                if (hourlyInfos.Count > 1)
                {
                    if (hourlyInfos[0].Price < hourlyInfos[1].Price && hourlyInfos[0].Price < averagePrice)
                    {
                        sessions.AddNewSession(Modes.Charging, hourlyInfos[0], averagePrice);
                    }

                    if (hourlyInfos[0].Price > hourlyInfos[1].Price && hourlyInfos[0].Price > averagePrice)
                    {
                        sessions.AddNewSession(Modes.Discharging, hourlyInfos[0], averagePrice);
                    }
                }

                // Check the elements in between.
                for (var i = 1; i < hourlyInfos.Count - 1; i++)
                {
                    if (hourlyInfos[i].Price < hourlyInfos[i - 1].Price && hourlyInfos[i].Price < hourlyInfos[i + 1].Price)
                    {
                        if (hourlyInfos[i].Price < averagePrice)
                            sessions.AddNewSession(Modes.Charging, hourlyInfos[i], averagePrice);
                    }

                    if (hourlyInfos[i].Price > hourlyInfos[i - 1].Price && hourlyInfos[i].Price > hourlyInfos[i + 1].Price)
                    {
                        if (hourlyInfos[i].Price > averagePrice)
                            sessions.AddNewSession(Modes.Discharging, hourlyInfos[i], averagePrice);
                    }
                }

                // Check the last element
                if (hourlyInfos.Count > 1)
                {
                    if (hourlyInfos[hourlyInfos.Count - 1].Price < hourlyInfos[hourlyInfos.Count - 2].Price && hourlyInfos[hourlyInfos.Count - 1].Price < averagePrice)
                        sessions.AddNewSession(Modes.Charging, hourlyInfos[hourlyInfos.Count - 1], averagePrice);

                    if (hourlyInfos[hourlyInfos.Count - 1].Price > hourlyInfos[hourlyInfos.Count - 2].Price && hourlyInfos[hourlyInfos.Count - 1].Price > averagePrice)
                        sessions.AddNewSession(Modes.Discharging, hourlyInfos[hourlyInfos.Count - 1], averagePrice);
                }
            }

            return sessions;
        }
    }
}
