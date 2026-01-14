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

        private SolarInverterManager _solarInverterManager { get; set; }
        private BatteryContainer _batteryContainer { get; set; }

        private LoggingService<ConsumptionMonitorService> _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private IOptionsMonitor<SessyP1Config> _sessyP1ConfigMonitor { get; set; }

        private SessyP1Config _sessyP1Config { get; set; }

        private IOptionsMonitor<SettingsConfig> _settingConfigMonitor { get; set; }

        private SettingsConfig _settingConfig { get; set; }

        public delegate Task DataChangedDelegate();

        public event DataChangedDelegate? DataChanged;

        private SemaphoreSlim _p1Semaphore = new SemaphoreSlim(1, 1);

        public ConsumptionMonitorService(LoggingService<ConsumptionMonitorService> logger,
                                         WeatherService weatherService,
                                         TimeZoneService timeZoneService,
                                         IOptionsMonitor<SessyP1Config> sessyP1ConfigMonitor,
                                         IOptionsMonitor<SettingsConfig> settingConfigMonitor,
                                         IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _weatherService = weatherService;
            _timeZoneService = timeZoneService;
            _sessyP1ConfigMonitor = sessyP1ConfigMonitor;
            _sessyP1Config = sessyP1ConfigMonitor.CurrentValue;
            _settingConfigMonitor = settingConfigMonitor;

            _sessyP1ConfigMonitor.OnChange(config =>
            {
                _p1Semaphore.Wait();

                try
                {
                    _sessyP1Config = config;
                }
                finally
                {
                    _p1Semaphore.Release();
                }
            });

            _settingConfig = settingConfigMonitor.CurrentValue;

            settingConfigMonitor.OnChange(config =>
            {
                _p1Semaphore.Wait();

                try
                {
                    _settingConfig = config;
                }
                finally
                {
                    _p1Semaphore.Release();
                }
            });

            _serviceScopeFactory = serviceScopeFactory;

            _scope = _serviceScopeFactory.CreateScope();

            _p1MeterService = _scope.ServiceProvider.GetRequiredService<P1MeterService>();
            _p1MeterContainer = new P1MeterContainer(_sessyP1ConfigMonitor, _p1MeterService);
            _consumptionDataService = _scope.ServiceProvider.GetRequiredService<ConsumptionDataService>();
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
                await EnsureServicesAreInitialized(cancelationToken);

                foreach (P1Meter? p1Meter in _p1MeterContainer.P1Meters!.ToList())
                {
                    await StoreConsumption(p1Meter);
                }

            }
            finally
            {
                _p1Semaphore.Release();
            }
        }

        public async Task EnsureServicesAreInitialized(CancellationToken cancelationToken)
        {
            var tries = 0;

            while (!_weatherService.Initialized && tries++ < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancelationToken);
            }

            if (!_weatherService.Initialized)
                throw new InvalidOperationException("Weather service not initialized");
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

        private async Task StoreConsumption(P1Meter p1Meter)
        {
            var startQuarter = _timeZoneService.Now.DateFloorQuarter();

            if (nextQuarter == null)
            {
                // Initialize first time
                nextQuarter = _timeZoneService.Now.DateCeilingQuarter();
            }

            if (_timeZoneService.Now >= nextQuarter)
            {
                var test = nextQuarter;
                nextQuarter = _timeZoneService.Now.DateCeilingQuarter();

                if (_consumptionData.Count > 0)
                {
                    var list = _consumptionData
                                                .Where(c => !double.IsNaN(c.ConsumptionWh) && !double.IsInfinity(c.ConsumptionWh));
                    var averageConsumptionWh = list.Count() > 0 ? list.Average(c => c.ConsumptionWh) : 0.0;

                    if (averageConsumptionWh > 0)
                    {
                        var p1Details = await _p1MeterContainer.GetDetails(p1Meter.Id!);
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

                        _logger.LogWarning($"Consumption is negative {averageConsumptionWh}. Cleared data");
                    }
                }
            }
            else
            {
                _consumptionData.Add(new ConsumptionData
                {
                    Time = startQuarter,
                    ConsumptionWh = await CalculateConsumption()
                });
            }
        }

        public async Task<double> CalculateConsumption()
        {
            double solarPower = 0.0;

            if(_timeZoneService.GetSunlightLevel(_settingConfig.Latitude, _settingConfig.Longitude) == SolCalc.Data.SunlightLevel.Daylight)
            {
                solarPower = await _solarInverterManager.GetTotalACPowerInWatts();
            }

            var netPower = await _p1MeterContainer.GetTotalPowerInWatts();
            var batteryPower = await _batteryContainer.GetTotalPowerInWatts();

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

            var taskList = quarterlyInfos.Select(qi => CalculateEstimatedConsumptionForAQuarterHour(consumptions, currentWeather!, qi)).ToList();

            await Task.WhenAll(taskList);
        }

        public async Task CalculateEstimatedConsumptionForAQuarterHour(List<Consumption> consumptions, WeerData currentWeather, QuarterlyInfo quarterlyInfo)
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

                    var list = consumptions
                            .Where(c => c.Time.Year >= minYear &&
                                        c.Time.Hour >= minHour &&
                                        c.Time.Hour <= maxHour &&
                                        c.Time.DayOfWeek == dayOfWeek &&
                                        c.ConsumptionWh > 0.0)
                            .ToList();

                    var subset = list.Where(c => c.Temperature >= minTemperature &&
                                                 c.Temperature <= maxTemperature &&
                                                 c.Humidity >= minHumidity &&
                                                 c.Humidity <= maxHumidity &&
                                                 c.GlobalRadiation >= minGlobalRadiation &&
                                                 c.GlobalRadiation <= maxGlobalRadiation)
                                     .ToList();

                    if (subset == null || subset.Count == 0)
                    {
                        subset = list.Where(c => c.Temperature >= minTemperature &&
                                                 c.Temperature <= maxTemperature &&
                                                 c.GlobalRadiation >= minGlobalRadiation &&
                                                 c.GlobalRadiation <= maxGlobalRadiation)
                                    .ToList();
                    }

                    if (subset == null || subset.Count == 0)
                    {
                        subset = list.Where(c => c.Temperature >= minTemperature &&
                                                 c.Temperature <= maxTemperature)
                                    .ToList();
                    }

                    if (subset != null && subset.Count > 0)
                    {
                        var average = subset.Average(c => c.ConsumptionWh) / 4.0; // Per quarter hour

                        if (average > 0)
                        {
                            quarterlyInfo.EstimatedConsumptionPerQuarterInWatts = average;
                            return;
                        }
                    }
                }

                quarterlyInfo.EstimatedConsumptionPerQuarterInWatts = _settingConfig.EnergyNeedsPerMonth / 96.0; // Default to average monthly consumption
            }

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
