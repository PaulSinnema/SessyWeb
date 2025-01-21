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

                if (!(_dayAheadMarketService.PricesAvailable && currentHourlyPrice != null))
                {
                    _logger.LogWarning("No prices available from ENTSO-E, switching to manual charging");

                    HandleManualCharging();
                }
                else
                {
                    await HandleAutomaticCharging();
                }
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
                await CheckIfBatteriesAreFull(currentHourlyPrice);

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
        /// </summary>
        private async Task CheckIfBatteriesAreFull(HourlyPrice currentHourlyPrice)
        {
            var stateOfCharge = await _batteryContainer.GetStateOfCharge();

            if (currentHourlyPrice.Charging && stateOfCharge == 1) // Batteries are full
            {
                var enumPrices = hourlyPrices.GetEnumerator();

                do
                {
                    if (!enumPrices.MoveNext())
                        return;

                } while (enumPrices.Current.Time.Hour < currentHourlyPrice.Time.Hour);

                while (enumPrices.Current.Charging == true)
                {
                    enumPrices.Current.Charging = false; // Stop charging

                    if (!enumPrices.MoveNext())
                        return;
                }
            }
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
                                    hourlyPrices[nextPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                    highestPrices.Add(nextPrice++);
                                }
                                else
                                {
                                    hourlyPrices[prevPrice].Discharging = CheckPrices(hourlyPrices[prevPrice], hourlyPrices[price]);
                                    hourlyPrices[prevPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                    highestPrices.Add(prevPrice--);
                                }
                            }
                            else if (prevPrice < 0)
                            {
                                hourlyPrices[nextPrice].Discharging = CheckPrices(hourlyPrices[nextPrice], hourlyPrices[price]);
                                hourlyPrices[nextPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                highestPrices.Add(nextPrice++);
                            }
                            else
                            {
                                hourlyPrices[prevPrice].Discharging = CheckPrices(hourlyPrices[prevPrice], hourlyPrices[price]);
                                hourlyPrices[prevPrice].HoursCharging = (hourlyPrices[price].HoursCharging);
                                highestPrices.Add(prevPrice--);
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
