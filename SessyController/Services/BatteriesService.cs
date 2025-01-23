using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;

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
        private static List<HourlyPrice>? hourlyPrices { get; set; }

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
                hourlyPrices = new List<HourlyPrice>();
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
            double totalBatteryCapacity = _batteryContainer.GetTotalCapacity();
            double chargingPower = _batteryContainer.GetChargingCapacity();
            double dischargingPower = _batteryContainer.GetDischargingCapacity();

            int maxChargingHours = (int)Math.Ceiling(totalBatteryCapacity / chargingPower);
            int maxDischargingHours = (int)Math.Ceiling(totalBatteryCapacity / dischargingPower);

            var pricesSortedByDate = hourlyPrices?.OrderBy(hp => hp.Time).ToList();

            List<int> lowestPrices = new List<int>();
            List<int> highestPrices = new List<int>();

            var averagePrice = hourlyPrices == null ? 0.0 : hourlyPrices.Average(hp => hp.Price);

            SearchForLowestAndHighestPrices(hourlyPrices, lowestPrices, highestPrices, averagePrice);

            foreach (var price in lowestPrices)
            {
                hourlyPrices[price].Charging = true;
            }

            foreach (var price in highestPrices)
            {
                hourlyPrices[price].Discharging = true;
            }

            CheckDischargeHours(hourlyPrices, lowestPrices, highestPrices);

            LowestHoursLeftAndRight(hourlyPrices, lowestPrices, maxChargingHours);

            HighestHoursLeftAndRight(hourlyPrices, highestPrices, maxDischargingHours);

            CheckCapacity(hourlyPrices);

            OptimizeChargingSessions(hourlyPrices);
        }

        public static void OptimizeChargingSessions(List<HourlyPrice> hourlyPrices)
        {
            var chargingSessions = new List<List<HourlyPrice>>();
            var chargingAbundance = new List<List<HourlyPrice>>();

            List<HourlyPrice> currentSession = new();

            // 🔍 Stap 1: Groepeer aaneengesloten charging-uren in sessies
            foreach (var hour in hourlyPrices)
            {
                if (hour.Charging)
                {
                    currentSession.Add(hour);
                }
                else if (currentSession.Count > 0)
                {
                    chargingSessions.Add(new List<HourlyPrice>(currentSession));
                    currentSession.Clear();
                }
            }

            if (currentSession.Count > 0)
                chargingSessions.Add(currentSession);

            foreach (var chargingSession in chargingSessions)
            {
                if (chargingSession.Count > 3)
                {
                    var session = chargingSession.OrderBy(cs => cs.Price).ToList();

                    for (int i = 3; i < session.Count; i++)
                    {
                        session[i].Charging = false;
                    }
                }
            }
        }

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
        /// Determine when the prices are the highest en the lowest.
        /// </summary>
        private static void SearchForLowestAndHighestPrices(List<HourlyPrice>? hourlyPrices, List<int> lowestPrices, List<int> highestPrices, double averagePrice)
        {
            if (hourlyPrices != null && hourlyPrices.Count > 0)
            {
                // Controleer eerste element
                if (hourlyPrices.Count > 1)
                {
                    if (hourlyPrices[0].Price < hourlyPrices[1].Price && hourlyPrices[0].Price < averagePrice)
                        lowestPrices.Add(0);

                    if (hourlyPrices[0].Price > hourlyPrices[1].Price && hourlyPrices[0].Price > averagePrice)
                        highestPrices.Add(0);
                }

                // Controleer de tussenliggende elementen
                for (var i = 1; i < hourlyPrices.Count - 1; i++)
                {
                    if (hourlyPrices[i].Price < hourlyPrices[i - 1].Price && hourlyPrices[i].Price < hourlyPrices[i + 1].Price)
                    {
                        if (hourlyPrices[i].Price < averagePrice)
                            lowestPrices.Add(i);
                    }

                    if (hourlyPrices[i].Price > hourlyPrices[i - 1].Price && hourlyPrices[i].Price > hourlyPrices[i + 1].Price)
                    {
                        if (hourlyPrices[i].Price > averagePrice)
                            highestPrices.Add(i);
                    }
                }

                // Controleer laatste element
                if (hourlyPrices.Count > 1)
                {
                    if (hourlyPrices[hourlyPrices.Count - 1].Price < hourlyPrices[hourlyPrices.Count - 2].Price && hourlyPrices[hourlyPrices.Count - 1].Price < averagePrice)
                        lowestPrices.Add(hourlyPrices.Count - 1);

                    if (hourlyPrices[hourlyPrices.Count - 1].Price > hourlyPrices[hourlyPrices.Count - 2].Price && hourlyPrices[hourlyPrices.Count - 1].Price > averagePrice)
                        highestPrices.Add(hourlyPrices.Count - 1);
                }
            }
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
