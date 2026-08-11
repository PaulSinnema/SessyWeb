using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Data;
using static SessyController.Services.WeatherService;

namespace SessyController.Services
{
    public class ConsumptionMonitorService : BackgroundService
    {
        private const int _hourDelta = 2;
        private const double _humidityDelta = 10.0;
        private const double _temperatureDelta = 4.0;
        private const double _globalRadiationDelta = 100.0;

        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        public WeatherService? _weatherService { get; set; }
        private IServiceScope _scope { get; set; }

        private P1MeterService _p1MeterService { get; set; }
        private P1MeterContainer _p1MeterContainer { get; set; }

        private ConsumptionDataService _consumptionDataService { get; set; }
        private EPEXPricesDataService _epexPricesDataService { get; set; }

        private SolarInverterManager _solarInverterManager { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private SystemCapabilitiesService _capabilities { get; set; }

        private LoggingService<ConsumptionMonitorService> _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }

        private SettingsService _settingsService;
        private Settings _settingConfig;

        public delegate Task DataChangedDelegate();

        public event DataChangedDelegate? DataChanged;

        private SemaphoreSlim _p1Semaphore = new SemaphoreSlim(1, 1);

        public ConsumptionMonitorService(LoggingService<ConsumptionMonitorService> logger,
                                         WeatherService weatherService,
                                         TimeZoneService timeZoneService,
                                         P1MeterContainer p1MeterContainer,
                                         SettingsService settingsService,
                                         SystemCapabilitiesService capabilities,
                                         IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _weatherService = weatherService;
            _timeZoneService = timeZoneService;
            _capabilities = capabilities;

            _settingsService = settingsService;
            _settingConfig = _settingsService.Current;

            _settingsService.SettingsChanged += (s, _) =>
            {
                _p1Semaphore.Wait();
                try
                {
                    _settingConfig = s;
                }
                finally
                {
                    _p1Semaphore.Release();
                }
            };

            _serviceScopeFactory = serviceScopeFactory;

            _scope = _serviceScopeFactory.CreateScope();

            _p1MeterService = _scope.ServiceProvider.GetRequiredService<P1MeterService>();
            _p1MeterContainer = p1MeterContainer;
            _consumptionDataService = _scope.ServiceProvider.GetRequiredService<ConsumptionDataService>();
            _epexPricesDataService = _scope.ServiceProvider.GetRequiredService<EPEXPricesDataService>();
            _solarInverterManager = _scope.ServiceProvider.GetRequiredService<SolarInverterManager>();
            _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
        }

        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("Consumption monitor service started ...");

            await RemoveNegativeConsumptionData();

            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while monitoring p1 meters.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred during delay of thread.");
                    // Ignore exception during delay
                }
            }

            _logger.LogWarning("Consumption monitor service stopped");
        }

        private async Task RemoveNegativeConsumptionData()
        {
            var list = await _consumptionDataService.GetList(async (set) =>
            {
                var result = set.Where(cd => cd.ConsumptionWh < 0).ToList();

                return await Task.FromResult(result);
            });

            if (list.Any())
            {
                list.ForEach(cd => _logger.LogWarning($"Removing consumption data: {cd}"));

                await _consumptionDataService.Remove(list, (item, set) =>
                {
                    return set.Where(cd => cd.Id == item.Id).FirstOrDefault();
                });

                _logger.LogWarning($"Removed {list.Count} negative consumption data rows.");
            }
        }

        private async Task Process(CancellationToken cancelationToken)
        {
            await _p1Semaphore.WaitAsync();

            try
            {
                await WaitForWeatherAsync(cancelationToken);

                var meters = _p1MeterContainer.P1Meters?.ToList() ?? new List<P1Meter>();

                if (meters.Count == 0)
                {
                    // Without this the loop simply iterates nothing and says nothing — the silent
                    // failure behind issue #4.
                    if (!_noMetersLogged)
                    {
                        _logger.LogWarning("No P1 meter is configured; no consumption is being recorded.");
                        _noMetersLogged = true;
                    }

                    return;
                }

                _noMetersLogged = false;

                foreach (P1Meter? p1Meter in meters)
                {
                    await StoreConsumption(p1Meter);
                }
            }
            finally
            {
                _p1Semaphore.Release();
            }
        }

        private bool _noMetersLogged = false;
        private bool _solarWasMeasurable = true;
        private bool _weatherWasAvailable = true;
        private bool _weatherGracePeriodDone = false;

        /// <summary>
        /// Weather is stored alongside consumption but is not what is being measured, so a missing
        /// feed must not stop recording. This used to throw, which aborted the whole cycle before a
        /// single quarter was stored — an empty Consumption page and no explanation (issue #4).
        /// Absent weather is written as the -999 sentinel the columns already use.
        ///
        /// The wait runs once, at startup, to give the feed a chance to arrive; afterwards a broken
        /// feed must not stall the one-second sampling loop.
        /// </summary>
        public async Task<bool> WaitForWeatherAsync(CancellationToken cancelationToken)
        {
            if (!_weatherGracePeriodDone)
            {
                var tries = 0;

                while (!_weatherService.IsInitialized() && tries++ < 10)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancelationToken);
                }

                _weatherGracePeriodDone = true;
            }

            var available = _weatherService.IsInitialized();

            if (!available && _weatherWasAvailable)
                _logger.LogWarning("No weather data available; consumption is stored without weather values.");

            _weatherWasAvailable = available;

            return available;
        }

        private class ConsumptionData
        {
            public DateTime Time { get; set; }
            public double ConsumptionWh { get; set; }

            public override string ToString()
            {
                return $"Time: {Time}, ConsumptionWh: {ConsumptionWh}";
            }
        }

        private List<ConsumptionData> _consumptionData = new List<ConsumptionData>();
        private DateTime? nextQuarter = null;

        /// <summary>
        /// Quarters discarded since start because the computed consumption was not positive, and the
        /// last one. Read by ConfigurationCheckService: a dropped quarter is the only direct evidence
        /// that a term is missing from solar + grid + battery, and the log alone is not visible in
        /// the UI.
        /// </summary>
        public int NegativeConsumptionQuarters { get; private set; }
        public DateTime? LastNegativeConsumptionAt { get; private set; }

        private async Task StoreConsumption(P1Meter p1Meter)
        {
            var startQuarter = _timeZoneService.Now.DateFloorQuarter();

            if (nextQuarter == null)
            {
                // Initialize first time.
                nextQuarter = _timeZoneService.Now.DateCeilingQuarter();
            }

            if (_timeZoneService.Now >= nextQuarter)
            {
                nextQuarter = _timeZoneService.Now.DateCeilingQuarter();

                if (_consumptionData.Count > 0)
                {
                    var list = _consumptionData
                        .Where(c => !double.IsNaN(c.ConsumptionWh) && !double.IsInfinity(c.ConsumptionWh));

                    var averageConsumptionWh = list.Count() > 0 ? list.Average(c => c.ConsumptionWh) : 0.0;

                    if (averageConsumptionWh > 0)
                    {
                        var weatherData = await _weatherService.GetWeatherData();

                        var liveWeer = weatherData?.LiveWeer?.FirstOrDefault();

                        _consumptionData.Clear();

                        var consumptionList = new List<Consumption>();

                        consumptionList.Add(new Consumption
                        {
                            Time = startQuarter,
                            ConsumptionWh = averageConsumptionWh,
                            // In case no weather data is present we store a large negative number.
                            Humidity = liveWeer?.Luchtvochtigheid ?? -999,
                            Temperature = liveWeer?.Temp ?? -999,
                            GlobalRadiation = liveWeer?.GlobalRadiation ?? -999
                        });

                        await _consumptionDataService.AddRange(consumptionList);

                        DataChanged?.Invoke();

                        _logger.LogInformation($"Consumption data stored for {startQuarter}");
                    }
                    else
                    {
                        _consumptionData.Clear();

                        NegativeConsumptionQuarters++;
                        LastNegativeConsumptionAt = startQuarter;

                        // Naming the cause matters: without an inverter the solar term is 0, so the
                        // sum goes negative in every quarter with net export and the Consumption page
                        // is empty exactly while the panels produce (issue #4).
                        var cause = _capabilities.HasSolar
                            ? "solar, grid and battery do not add up to a household load — check that all three are being read"
                            : "no inverter is configured, so solar production is missing from the sum and any net export to the grid makes it negative";

                        _logger.LogWarning($"Consumption for {startQuarter} came out at {averageConsumptionWh:F0} W; " +
                                           $"nothing stored. Cause: {cause}.");
                    }
                }
            }
            else
            {
                var consumptionWh = await CalculateConsumption();

                // Null means a term could not be read. Recording it as 0.0 would drag the quarter
                // average down and store a number that is simply wrong, so the sample is dropped.
                if (consumptionWh.HasValue)
                {
                    _consumptionData.Add(new ConsumptionData
                    {
                        Time = startQuarter,
                        ConsumptionWh = consumptionWh.Value
                    });
                }
            }
        }

        /// <summary>
        /// Household consumption is solar production + grid draw + battery discharge. Every term is
        /// needed: a battery that cannot be read while it discharges 3 kW would understate the
        /// quarter by exactly that. Returns null rather than a partial sum.
        /// </summary>
        public async Task<double?> CalculateConsumption()
        {
            double solarPower = 0.0;
            double netPower = 0.0;
            double batteryPower = 0.0;
            bool error = false;

            try
            {
                solarPower = await _solarInverterManager.GetTotalACPowerInWatts();

                // An unreachable inverter answers 0 W, which reads as darkness in the sum and takes
                // the whole PV production out of the household load without any exception to catch.
                if (!_solarInverterManager.SolarIsMeasurable)
                {
                    if (_solarWasMeasurable)
                        _logger.LogWarning("Inverter is unreachable in daylight; its 0 W is not a measurement, " +
                                           "so consumption is not recorded until it answers again.");

                    _solarWasMeasurable = false;
                    error = true;
                }
                else
                {
                    _solarWasMeasurable = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "Could not get Solarpower");
                error = true;
            }

            try
            {
                netPower = await _p1MeterContainer.GetTotalPowerInWatts();
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "Could not get net power");
                error = true;
            }

            try
            {
                batteryPower = await _batteryContainer.GetTotalPowerInWatts();
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, "Could not get battery power");
                error = true;
            }

            if (error)
            {
                return null; // Some value(s) missing; a partial sum is not a measurement.
            }

            return solarPower + netPower + batteryPower;
        }

        public async Task<double> GetHumidity(double? temperature)
        {
            var list = await _consumptionDataService.GetList(async (set) =>
            {
                var result = set
                    .Where(c => c.Temperature >= temperature - _temperatureDelta &&
                                c.Temperature <= temperature + _temperatureDelta)
                    .ToList();

                return await Task.FromResult(result);
            });

            return list.Count > 0 ? list.Average(c => c.Humidity) : 0.0;
        }

        private class ConsumptionCacheItem
        {
            public DateTime FirstTime { get; set; }
            public DateTime LastTime { get; set; }
            public List<Consumption> Consumptions { get; set; } = new List<Consumption>();
        }

        public async Task EstimateConsumptionInWattsPerQuarter(List<QuarterlyInfo> quarterlyInfos)
        {
            var currentWeather = await _weatherService.GetWeatherData();
            List<Consumption> consumptions = await GetDataForAYear();

            // Fetch EPEX prices for the same period and build a lookup by quarter time.
            var now = _timeZoneService.Now;
            var priceStart = now.AddDays(-365);
            var priceEnd = now.AddDays(2);

            var epexPrices = await _epexPricesDataService.GetList(async (set) =>
            {
                var result = set
                    .Where(p => p.Time >= priceStart && p.Time <= priceEnd && p.Price.HasValue)
                    .ToList();
                return await Task.FromResult(result);
            });

            var priceLookup = epexPrices
                .ToDictionary(p => p.Time, p => p.Price!.Value);

            var taskList = quarterlyInfos
                .Select(qi => CalculateEstimatedConsumptionForAQuarterHour(consumptions, priceLookup, currentWeather, qi))
                .ToList();

            await Task.WhenAll(taskList);
        }

        /// <summary>
        /// Estimates one quarter from comparable historical quarters. Without weather there is
        /// nothing to compare on, so the monthly profile from Settings is used — that fallback used
        /// to sit inside the weather branch, which left the estimate at 0 W exactly when no weather
        /// was available and told the planner the house needs nothing.
        /// </summary>
        public async Task CalculateEstimatedConsumptionForAQuarterHour(
            List<Consumption> consumptions,
            Dictionary<DateTime, double> priceLookup,
            WeerData? currentWeather,
            QuarterlyInfo quarterlyInfo)
        {
            var minHour = quarterlyInfo.Time.Hour - _hourDelta;
            var maxHour = quarterlyInfo.Time.Hour + _hourDelta;
            var minYear = quarterlyInfo.Time.Year - 1;
            var dayOfWeek = quarterlyInfo.Time.DayOfWeek;

            if (currentWeather != null)
            {
                var uurverwachting = currentWeather.UurVerwachting
                    .FirstOrDefault(u => u.TimeStamp!.Value.Hour == quarterlyInfo.Time.Hour &&
                                         u.TimeStamp!.Value.DayOfWeek == quarterlyInfo.Time.DayOfWeek);

                if (uurverwachting != null)
                {
                    double? minTemperature = uurverwachting.Temp - _temperatureDelta;
                    double? maxTemperature = uurverwachting.Temp + _temperatureDelta;
                    double? humidity = await GetHumidity(uurverwachting.Temp);
                    double? minHumidity = humidity - _humidityDelta;
                    double? maxHumidity = humidity + _humidityDelta;
                    double? minGlobalRadiation = uurverwachting.GlobalRadiation - _globalRadiationDelta;
                    double? maxGlobalRadiation = uurverwachting.GlobalRadiation + _globalRadiationDelta;

                    double currentPrice = quarterlyInfo.BuyingPrice;

                    // Base filter: time window, day of week, positive consumption.
                    var list = consumptions
                        .Where(c => c.Time.Year >= minYear &&
                                    c.Time.Hour >= minHour &&
                                    c.Time.Hour <= maxHour &&
                                    c.Time.DayOfWeek == dayOfWeek &&
                                    c.ConsumptionWh > 0.0)
                        .ToList();

                    // Fallback chain: progressively relax filters until a match is found.
                    // Price is used as a soft tie-breaker within each subset — records with
                    // a closer historical price are ranked higher but never fully excluded.
                    // This avoids over-filtering when few historical records exist at
                    // extreme prices (e.g. very low or negative prices).

                    List<Consumption>? subset = null;

                    // 1. Full match: temperature + humidity + global radiation.
                    var candidates = list
                        .Where(c => c.Temperature >= minTemperature &&
                                    c.Temperature <= maxTemperature &&
                                    c.Humidity >= minHumidity &&
                                    c.Humidity <= maxHumidity &&
                                    c.GlobalRadiation >= minGlobalRadiation &&
                                    c.GlobalRadiation <= maxGlobalRadiation)
                        .ToList();

                    // 2. Without humidity.
                    if (candidates.Count == 0)
                    {
                        candidates = list
                            .Where(c => c.Temperature >= minTemperature &&
                                        c.Temperature <= maxTemperature &&
                                        c.GlobalRadiation >= minGlobalRadiation &&
                                        c.GlobalRadiation <= maxGlobalRadiation)
                            .ToList();
                    }

                    // 3. Only temperature.
                    if (candidates.Count == 0)
                    {
                        candidates = list
                            .Where(c => c.Temperature >= minTemperature &&
                                        c.Temperature <= maxTemperature)
                            .ToList();
                    }

                    // Apply soft price weighting: sort by price proximity and take the
                    // closest matches. Records without a known historical price are ranked last.
                    if (candidates.Count > 0)
                    {
                        subset = candidates
                            .OrderBy(c => priceLookup.TryGetValue(c.Time, out var p)
                                ? Math.Abs(p - currentPrice)
                                : double.MaxValue)
                            .Take(10)
                            .ToList();
                    }

                    if (subset != null && subset.Count > 0)
                    {
                        var average = subset.Average(c => c.ConsumptionWh); // Already in Watts

                        if (average > 0)
                        {
                            quarterlyInfo.EstimatedConsumptionPerQuarterInWatts = average;
                            return;
                        }
                    }
                }
            }

            quarterlyInfo.EstimatedConsumptionPerQuarterInWatts = _settingConfig.EnergyNeedsForCurrentMonth(_timeZoneService.Now.Month - 1) / 24.0; // Wh/day ÷ 24 hours = average Watts
        }

        private async Task<List<Consumption>> GetDataForAYear()
        {
            var now = _timeZoneService.Now;
            var start = now.AddDays(-365);
            var end = now.AddDays(1);

            var dataset = await _consumptionDataService.GetList(async (set) =>
            {
                var result = set
                    .Where(c => c.Time >= start && c.Time <= end)
                    .ToList();
                return await Task.FromResult(result);
            });
            return dataset;
        }
    }
}