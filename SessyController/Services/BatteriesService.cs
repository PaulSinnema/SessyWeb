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
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly SettingsConfig? _settingsConfig;
        private readonly SessyBatteryConfig _sessyBatteryConfig;
        private readonly BatteryContainer _batteryContainer;
        private readonly LoggingService<BatteriesService> _logger;
        private readonly TimeZoneInfo _timeZone;
        private static List<HourlyPrice> hourlyPrices { get; set; } = new List<HourlyPrice>();

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

            if (!string.IsNullOrWhiteSpace(_settingsConfig?.Timezone))
                _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_settingsConfig.Timezone);
            else
                throw new InvalidOperationException("Timezone is missing");

            using (var scope = _serviceScopeFactory.CreateScope())
            {
                _sessyService = scope.ServiceProvider.GetRequiredService<SessyService>();
                _p1MeterService = scope.ServiceProvider.GetRequiredService<P1MeterService>();
                _dayAheadMarketService = scope.ServiceProvider.GetRequiredService<DayAheadMarketService>();
                _solarEdgeService = scope.ServiceProvider.GetRequiredService<SolarEdgeService>();
                _batteryContainer = scope.ServiceProvider.GetRequiredService<BatteryContainer>();
            }
        }

        /// <summary>
        /// Executes the background service, fetching prices periodically.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogInformation("EPEXHourlyPricesService started.");

            // Loop to fetch prices every hour
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
#if !DEBUG
                    await
#endif
                    Process(cancelationToken);
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
        public
#if !DEBUG
            async Task
#else
            void
#endif
            Process(CancellationToken cancellationToken)
        {
            if (_dayAheadMarketService.PricesInitialized)
            {
                DetermineChargingHours();

                HourlyPrice? currentHourlyPrice = GetCurrentHourlyPrice();

#if !DEBUG
                if (!(_dayAheadMarketService.PricesAvailable && currentHourlyPrice != null))
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
        public List<HourlyPrice>? GetHourlyPrices()
        {
            return hourlyPrices;
        }

        private async Task HandleAutomaticCharging()
        {
            HourlyPrice? currentHourlyPrice = GetCurrentHourlyPrice();

            if (currentHourlyPrice != null)
            {
                await CancelSessionIfStateRequiresIt(currentHourlyPrice);

                if (currentHourlyPrice.Charging)
                    _batteryContainer.StartCharging();
                else if (currentHourlyPrice.Discharging)
                    _batteryContainer.StartDisharging();
                else if (currentHourlyPrice.ZeroNetHome)
                    _batteryContainer.StartNetZeroHome();
                else if (currentHourlyPrice.CapacityExhausted)
                    _batteryContainer.StopAll();
            }
        }

        private void HandleManualCharging()
        {
            var localTime = GetLocalTime();

            if (_settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                _batteryContainer.StartCharging();
            else if (_settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                _batteryContainer.StartDisharging();
            else if (_settingsConfig.ManualNetZeroHomeHours.Contains(localTime.Hour))
                _batteryContainer.StartNetZeroHome();
            else
                _batteryContainer.StopAll();
        }

        private HourlyPrice? GetCurrentHourlyPrice()
        {
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);

            return hourlyPrices?
                .FirstOrDefault(hp => hp.Time.Date == localTime.Date && hp.Time.Hour == localTime.Hour);
        }

        /// <summary>
        /// If batteries are full stop charging this session
        /// If batteries are empty stop discharging this session
        /// </summary>
        private async Task CancelSessionIfStateRequiresIt(HourlyPrice currentHourlyPrice)
        {
            if (currentHourlyPrice.Charging)
            {
                bool batteriesAreFull = await AreAllBattiesFull(currentHourlyPrice);

                if (batteriesAreFull) // Batteries are full
                    StopChargingSession(currentHourlyPrice);
            }
            else if (currentHourlyPrice.Discharging)
            {
                bool batteriesAreEmpty = await AreAllBattiesEmpty(currentHourlyPrice);

                if (batteriesAreEmpty)
                    StopDischargingSession(currentHourlyPrice);
            }
        }

        /// <summary>
        /// Cancel charging for current and future consequtive charging hours.
        /// </summary>
        private static void StopChargingSession(HourlyPrice currentHourlyPrice)
        {
            if (hourlyPrices != null)
            {
                var enumPrices = hourlyPrices.GetEnumerator();

                while (enumPrices.Current.Time.Hour < currentHourlyPrice.Time.Hour)
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
        private static void StopDischargingSession(HourlyPrice currentHourlyPrice)
        {
            if (hourlyPrices != null)
            {
                var enumPrices = hourlyPrices.GetEnumerator();

                while (enumPrices.Current.Time.Hour < currentHourlyPrice.Time.Hour)
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
        private async Task<bool> AreAllBattiesFull(HourlyPrice currentHourlyPrice)
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
        private async Task<bool> AreAllBattiesEmpty(HourlyPrice currentHourlyPrice)
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
        public bool DetermineChargingHours()
        {
            DateTime localTime = GetLocalTime();

            if (localTime.Date > lastDateChecked.Date)
                hasCheckedLastDate1600 = false;

            if (!hasCheckedLastDate1600 && localTime.Hour >= 16)
                hasCheckedLastDate1600 = false;

            var tomorrow = localTime.AddDays(1);

            var hoursArePresent = localTime.Hour >= 23 &&
                hourlyPrices?
                .Where(hp => hp.Time == tomorrow.Date)
                .Count() > 0;

            if (hasCheckedLastDate1600 && !hoursArePresent)
                hasCheckedLastDate1600 = false;

            if (!hasCheckedLastDate1600)
            {
                if (!FetchPricesFromENTSO_E(localTime))
                    return false;

                GetChargingHours();
                hasCheckedLastDate1600 = true;
                lastDateChecked = localTime;
            }

            return true;
        }

        private DateTime GetLocalTime()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        /// <param name="localTime"></param>
        /// <returns></returns>
        private bool FetchPricesFromENTSO_E(DateTime localTime)
        {
            // Get the available hourly prices from now.
            hourlyPrices = _dayAheadMarketService.GetPrices()
                .OrderBy(hp => hp.Time)
                .ToList();

            return hourlyPrices != null && hourlyPrices.Count > 0;
        }

        /// <summary>
        /// In this routine is determined when to charge the batteries.
        /// </summary>
        private void GetChargingHours()
        {
            hourlyPrices = hourlyPrices.OrderBy(hp => hp.Time).ToList();

            List<int> lowestPrices = new List<int>();
            List<int> highestPrices = new List<int>();

            var averagePrice = hourlyPrices.Average(hp => hp.Price);

            Sessions sessions = CreateSessions(hourlyPrices, averagePrice);

            EvaluateSessions(averagePrice, sessions);

            foreach (var session in sessions.SessionList.ToList())
            {
                foreach (var hourlyPrice in session.PriceList)
                {
                    hourlyPrice.Charging = session.Mode == Modes.Charging;
                    hourlyPrice.Discharging = session.Mode == Modes.Discharging;
                }
            }

            //CheckDischargeHours(hourlyPrices, lowestPrices, highestPrices);

            //LowestHoursLeftAndRight(hourlyPrices, lowestPrices, maxChargingHours);

            //HighestHoursLeftAndRight(hourlyPrices, highestPrices, maxDischargingHours);

            //CheckCapacity(hourlyPrices);

            //OptimizeChargingSessions(hourlyPrices);
        }

        private void EvaluateSessions(double averagePrice, Sessions sessions)
        {
            var changed = false;

            do
            {
                changed = false;

                changed = MergeSessions(sessions, averagePrice);

                changed = changed || RemoveExtraSessions(sessions);

                changed = changed || RemoveEmptySessions(sessions);

                changed = changed || CheckProfetability(sessions);
            } while (changed);
        }

        private bool CheckProfetability(Sessions sessions)
        {
            var changed = false;

            var sessionList = sessions.SessionList.OrderBy(se => se.First).ToList();

            for (int currentSession = 0; currentSession < sessionList.Count; currentSession++)
            {
                switch (sessionList[currentSession].Mode)
                {
                    case Modes.Charging:
                        {
                            if(currentSession + 1 < sessionList.Count)
                            {
                                if (sessionList[currentSession + 1].Mode == Modes.Discharging)
                                {
                                    var chargingHours = sessionList[currentSession].PriceList.OrderBy(hp => hp.Price).ToList();
                                    var dischargingHours = sessionList[currentSession + 1].PriceList.OrderByDescending(hp => hp.Price).ToList();
                                    var maxHours = Math.Min(chargingHours.Count, dischargingHours.Count);

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
                                                    sessions.SessionList[currentSession + 1].PriceList.Remove(dischargingEnumerator.Current);
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
                                                sessions.SessionList[currentSession + 1].PriceList.Remove(dischargingEnumerator.Current);
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

        private bool RemoveExtraSessions(Sessions sessions)
        {
            var changed = false;

            List<HourlyPrice> listToRemove;

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

        private bool MergeSessions(Sessions sessions, double averagePrice)
        {
            var changed = false;

            Session? lastSession = null;
            var list = sessions.SessionList
                        .Where(se => se.PriceList.Count > 0)
                        .OrderBy(se => se.First).ToList();

            foreach (var session in list)
            {
                if(lastSession != null)
                {
                    if(lastSession.Mode == session.Mode)
                    {
                        MergeSessions(session, lastSession);
                        changed = true;
                    }
                }

                lastSession = session;
            }

            return changed;
        }

        private void MergeSessions(Session lastSession, Session session)
        {
            foreach (var hourlyPrice in session.PriceList)
            {
                lastSession.PriceList.Add(hourlyPrice);
            }

            session.PriceList.Clear();
        }

        /// <summary>
        /// Determine when the prices are the highest en the lowest.
        /// </summary>
        private Sessions CreateSessions(List<HourlyPrice> hourlyPrices, double averagePrice)
        {
            double totalBatteryCapacity = _batteryContainer.GetTotalCapacity();
            double chargingPower = _batteryContainer.GetChargingCapacity();
            double dischargingPower = _batteryContainer.GetDischargingCapacity();
            int maxChargingHours = (int)Math.Ceiling(totalBatteryCapacity / chargingPower);
            int maxDischargingHours = (int)Math.Ceiling(totalBatteryCapacity / dischargingPower);

            Sessions sessions = new Sessions(hourlyPrices, maxChargingHours, maxDischargingHours, _settingsConfig.CycleCost);

            if (hourlyPrices != null && hourlyPrices.Count > 0)
            {
                // Controleer eerste element
                if (hourlyPrices.Count > 1)
                {
                    if (hourlyPrices[0].Price < hourlyPrices[1].Price && hourlyPrices[0].Price < averagePrice)
                    {
                        sessions.AddNewSession(Modes.Charging, hourlyPrices[0], averagePrice);
                    }

                    if (hourlyPrices[0].Price > hourlyPrices[1].Price && hourlyPrices[0].Price > averagePrice)
                    {
                        sessions.AddNewSession(Modes.Discharging, hourlyPrices[0], averagePrice);
                    }
                }

                // Controleer de tussenliggende elementen
                for (var i = 1; i < hourlyPrices.Count - 1; i++)
                {
                    if (hourlyPrices[i].Price < hourlyPrices[i - 1].Price && hourlyPrices[i].Price < hourlyPrices[i + 1].Price)
                    {
                        if (hourlyPrices[i].Price < averagePrice)
                            sessions.AddNewSession(Modes.Charging, hourlyPrices[i], averagePrice);
                    }

                    if (hourlyPrices[i].Price > hourlyPrices[i - 1].Price && hourlyPrices[i].Price > hourlyPrices[i + 1].Price)
                    {
                        if (hourlyPrices[i].Price > averagePrice)
                            sessions.AddNewSession(Modes.Discharging, hourlyPrices[i], averagePrice);
                    }
                }

                // Controleer laatste element
                if (hourlyPrices.Count > 1)
                {
                    if (hourlyPrices[hourlyPrices.Count - 1].Price < hourlyPrices[hourlyPrices.Count - 2].Price && hourlyPrices[hourlyPrices.Count - 1].Price < averagePrice)
                        sessions.AddNewSession(Modes.Charging, hourlyPrices[hourlyPrices.Count - 1], averagePrice);

                    if (hourlyPrices[hourlyPrices.Count - 1].Price > hourlyPrices[hourlyPrices.Count - 2].Price && hourlyPrices[hourlyPrices.Count - 1].Price > averagePrice)
                        sessions.AddNewSession(Modes.Discharging, hourlyPrices[hourlyPrices.Count - 1], averagePrice);
                }
            }

            OptimizeChargingSessions(sessions);

            return sessions;
        }

        private void OptimizeChargingSessions(Sessions sessions)
        {
        }

        //public static void OptimizeChargingSessions(Sessions sessions)
        //{
        //    foreach (var chargingSession in sessions.SessionList)
        //    {
        //        if (chargingSession.PriceList.Count > chargingSession.MaxHours)
        //        {
        //            var priceList = chargingSession.PriceList.OrderBy(cs => cs.Price).ToList();

        //            for (int i = 3; i < priceList.Count; i++)
        //            {
        //                switch (chargingSession.Mode)
        //                {
        //                    case Modes.Charging:
        //                        priceList[i].Charging = false;
        //                        break;

        //                    case Modes.Discharging:
        //                        priceList[i].Discharging = false;
        //                        break;

        //                    default:
        //                        break;
        //                }
        //            }
        //        }
        //    }
        //}

        static void CheckChargingWithoutDischargingHours(List<HourlyPrice> hourlyPrices)
        {
            List<List<HourlyPrice>> chargingSessions = new List<List<HourlyPrice>>();
            List<HourlyPrice> currentSession = new List<HourlyPrice>();

            // Stap 1: Groepeer de charging sessies
            foreach (var entry in hourlyPrices)
            {
                if (entry.Charging)
                {
                    currentSession.Add(entry);
                }
                else
                {
                    if (currentSession.Count > 0)
                    {
                        chargingSessions.Add(new List<HourlyPrice>(currentSession));
                        currentSession.Clear();
                    }
                }
            }
            if (currentSession.Count > 0)
            {
                chargingSessions.Add(new List<HourlyPrice>(currentSession));
            }

            // Stap 2: Check of er een discharging session tussen zit
            for (int i = 0; i < chargingSessions.Count - 1; i++)
            {
                DateTime endHour = chargingSessions[i].Last().Time;
                DateTime startNextSession = chargingSessions[i + 1].First().Time;

                // Controleer of er een discharging sessie tussen de twee charging sessies zit
                bool hasDischargingBetween = hourlyPrices.Any(h => h.Time > endHour && h.Time < startNextSession && h.Discharging);

                if (!hasDischargingBetween)
                {
                    // Bereken de gemiddelde prijs van beide charging sessions
                    double avgPriceFirst = chargingSessions[i].Average(h => h.Price);
                    double avgPriceSecond = chargingSessions[i + 1].Average(h => h.Price);

                    // Verwijder de charging session met de hoogste gemiddelde prijs
                    var sessionToRemove = avgPriceFirst > avgPriceSecond ? chargingSessions[i] : chargingSessions[i + 1];

                    foreach (var entry in sessionToRemove)
                    {
                        entry.Charging = false; // Zet deze op nzh (of een andere neutrale status)
                    }

                    // Verwijder de session uit de lijst om verdere checks correct uit te voeren
                    chargingSessions.Remove(sessionToRemove);

                    // Omdat we de lijst hebben aangepast, moeten we opnieuw door de lijst lopen
                    i--;
                }
            }
        }

        /// <summary>
        /// This routine estimates how much power must remain for discharging
        /// </summary>
        private void CheckCapacity(List<HourlyPrice>? hourlyPrices)
        {
            if (hourlyPrices != null)
            {
                HourlyPrice? currentHourlyPrice = GetCurrentHourlyPrice();

                if (currentHourlyPrice != null)
                {
                    List<HourlyPrice> dischargingHours = GetNextDischargingSession(hourlyPrices, currentHourlyPrice);

                    var list = dischargingHours.ToList();
                }
            }
        }

        private static List<HourlyPrice> GetNextDischargingSession(List<HourlyPrice> hourlyPrices, HourlyPrice currentHourlyPrice)
        {
            // Find all discharging hours after the current time.
            var dischargingHours = hourlyPrices
                .Where(hp => hp.Time > currentHourlyPrice.Time && hp.Discharging)
                .ToList();

            // Filter out discharging hours not belonging to the first discharging session.
            var dischargingEnumerator = dischargingHours.ToList().GetEnumerator();

            if (dischargingEnumerator.MoveNext()) // Load first
            {
                var first = dischargingEnumerator.Current;

                while (dischargingEnumerator.MoveNext()) // Load next
                {
                    if (dischargingEnumerator.Current.Time.Hour - first.Time.Hour > 1) // Not contiguous?
                    {
                        dischargingHours.Remove(dischargingEnumerator.Current);
                    }
                    else
                    {
                        first = dischargingEnumerator.Current;
                    }
                }
            }

            return dischargingHours;
        }


        /// <summary>
        /// Check the discharging hours if the cycle cost can be earned. If not, don't charge.
        /// </summary>
        private void CheckDischargeHours(List<HourlyPrice>? hourlyPrices, List<int> lowestPrices, List<int> highestPrices)
        {
            if (hourlyPrices != null)
            {
                var cycleCost = _settingsConfig.CycleCost;
                var intersecting = lowestPrices.Intersect(highestPrices);

                foreach (var item in intersecting)
                {
                    var index = highestPrices.IndexOf(item);

                    highestPrices.Remove(index);
                }

                lowestPrices.Sort();
                highestPrices.Sort();

                var lowestEnumerator = lowestPrices.GetEnumerator();
                var highestEnumerator = highestPrices.GetEnumerator();

                var hasLow = lowestEnumerator.MoveNext();
                var hasHigh = highestEnumerator.MoveNext();

                while (hasLow && hasHigh)
                {
                    var high = hourlyPrices[highestEnumerator.Current];
                    var low = hourlyPrices[lowestEnumerator.Current];

                    if (low.Time < high.Time)
                    {
                        // The lowest price is earlier then the highest price
                        if (!CheckPrices(low, high)) // The price does not justify discharging.
                            high.Discharging = false;

                        if (high.Discharging)
                        {
                            if (high.HoursCharging == null)
                                high.HoursCharging = new List<HourlyPrice>();

                            high.HoursCharging.Add(low);
                        }

                        hasLow = lowestEnumerator.MoveNext();
                    }
                    else
                        hasHigh = highestEnumerator.MoveNext();
                }
            }
        }

        /// <summary>
        /// Look at the prices before and after the lowest prices and add them for the duration
        /// of this charge session.
        /// </summary>
        private void LowestHoursLeftAndRight(List<HourlyPrice>? hourlyPrices, List<int> lowestPrices, int maxChargingHours)
        {
            if (hourlyPrices != null)
            {
                foreach (var price in lowestPrices.ToList())
                {
                    var maxPrice = maxChargingHours - 1;
                    var prevPrice = price - 1;
                    var nextPrice = price + 1;

                    while (maxPrice > 0)
                    {
                        if (prevPrice >= 0 && nextPrice < hourlyPrices.Count)
                        {
                            if (hourlyPrices[prevPrice].Price > hourlyPrices[nextPrice].Price)
                            {
                                hourlyPrices[nextPrice].Charging = hourlyPrices[price].Charging;
                                lowestPrices.Add(nextPrice++);
                            }
                            else
                            {
                                hourlyPrices[prevPrice].Charging = hourlyPrices[price].Charging;
                                lowestPrices.Add(prevPrice--);
                            }
                        }
                        else if (prevPrice < 0)
                        {
                            lowestPrices.Add(nextPrice++);
                        }
                        else
                        {
                            lowestPrices.Add(prevPrice--);
                        }

                        maxPrice--;
                    }
                }
            }
        }

        /// <summary>
        /// Look at the prices before and after the highest prices and add them for the duration
        /// of this charge session.
        /// </summary>
        private void HighestHoursLeftAndRight(List<HourlyPrice>? hourlyPrices, List<int> highestPrices, int maxChargingHours)
        {
            if (hourlyPrices != null)
            {
                foreach (var price in highestPrices.ToList())
                {
                    if (hourlyPrices[price].Discharging)
                    {
                        var maxPrice = maxChargingHours - 1;
                        var prevPrice = price - 1;
                        var nextPrice = price + 1;

                        while (maxPrice > 0)
                        {
                            if (prevPrice >= 0 && nextPrice <= hourlyPrices.Count - 1)
                            {
                                if (hourlyPrices[prevPrice].Price < hourlyPrices[nextPrice].Price)
                                {
                                    hourlyPrices[nextPrice].Discharging = CheckPrices(hourlyPrices[nextPrice], hourlyPrices[price]);

                                    if (hourlyPrices[nextPrice].Discharging)
                                    {
                                        hourlyPrices[nextPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                        highestPrices.Add(nextPrice);
                                    }
                                    nextPrice++;
                                }
                                else
                                {
                                    hourlyPrices[prevPrice].Discharging = CheckPrices(hourlyPrices[prevPrice], hourlyPrices[price]);

                                    if (hourlyPrices[prevPrice].Discharging)
                                    {
                                        hourlyPrices[prevPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                        highestPrices.Add(prevPrice);
                                    }
                                    prevPrice--;
                                }
                            }
                            else if (prevPrice < 0)
                            {
                                hourlyPrices[nextPrice].Discharging = CheckPrices(hourlyPrices[nextPrice], hourlyPrices[price]);

                                if (hourlyPrices[nextPrice].Discharging)
                                {
                                    hourlyPrices[nextPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                    highestPrices.Add(nextPrice);
                                }

                                nextPrice++;
                            }
                            else
                            {
                                hourlyPrices[prevPrice].Discharging = CheckPrices(hourlyPrices[prevPrice], hourlyPrices[price]);

                                if (hourlyPrices[prevPrice].Discharging)
                                {
                                    hourlyPrices[prevPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                    highestPrices.Add(prevPrice);
                                }
                                prevPrice--;
                            }

                            maxPrice--;
                        }
                    }
                }
            }
        }

        private bool CheckPrices(HourlyPrice hourlyPrice1, HourlyPrice hourlyPrice2)
        {
            return hourlyPrice1.Price + _settingsConfig.CycleCost <= hourlyPrice2.Price;
        }
    }
}
