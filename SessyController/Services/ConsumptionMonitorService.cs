using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Managers;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Threading;
using static P1MeterService;
using static SessyController.Services.WeatherService;

namespace SessyController.Services
{
    public class ConsumptionMonitorService : BackgroundService
    {
        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        public WeatherService? _weatherService { get; set; }
        private IServiceScope _scope { get; set; }

        private P1MeterService _p1MeterService { get; set; }
        private P1MeterContainer _p1MeterContainer { get; set; }

        private ConsumptionDataService _consumptionService { get; set; }

        private SolarInverterManager _solarInverterManager { get; set; }
        private BatteryContainer _batteryContainer { get; set; }

        private LoggingService<ConsumptionMonitorService> _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private IOptionsMonitor<SessyP1Config> _sessyP1ConfigMonitor { get; set; }
        private SessyP1Config _sessyP1Config { get; set; }

        public delegate Task DataChangedDelegate();

        public event DataChangedDelegate? DataChanged;

        public ConsumptionMonitorService(LoggingService<ConsumptionMonitorService> logger,
                                         WeatherService weatherService,
                                         TimeZoneService timeZoneService,
                                         IOptionsMonitor<SessyP1Config> sessyP1ConfigMonitor,
                                         IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _weatherService = weatherService;
            _timeZoneService = timeZoneService;
            _sessyP1ConfigMonitor = sessyP1ConfigMonitor;
            _sessyP1Config = sessyP1ConfigMonitor.CurrentValue;

            _sessyP1ConfigMonitor.OnChange(config =>
            {
                _sessyP1Config = config;
            });

            _serviceScopeFactory = serviceScopeFactory;

            _scope = _serviceScopeFactory.CreateScope();

            _p1MeterService = _scope.ServiceProvider.GetRequiredService<P1MeterService>();
            _p1MeterContainer = new P1MeterContainer(_sessyP1ConfigMonitor, _p1MeterService);
            _consumptionService = _scope.ServiceProvider.GetRequiredService<ConsumptionDataService>();
            _solarInverterManager = _scope.ServiceProvider.GetRequiredService<SolarInverterManager>();
            _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
        }
        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("Consumption monitor service started ...");

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
                    await Task.Delay(TimeSpan.FromSeconds(2), cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred during delay of thread.");
                    // Ignore exception during delay
                }
            }

            _logger.LogWarning("Consumption monitor service stopped");
        }

        private async Task Process(CancellationToken cancelationToken)
        {
            await EnsureServicesAreInitialized(cancelationToken);

            foreach (P1Meter? p1Meter in _p1MeterContainer.P1Meters)
            {
                var now = _timeZoneService.Now;
                var selectTime = now.DateFloorQuarter();

                var p1Details = await _p1MeterContainer.GetDetails(p1Meter.Id!);
                var weatherData = _weatherService.GetWeatherData();

                var liveWeer = weatherData?.LiveWeer?.FirstOrDefault();

                await StoreConsumption(p1Meter, p1Details!, selectTime, liveWeer);

                DataChanged?.Invoke();

                _logger.LogInformation($"Consumption data stored at {now} for {selectTime}");
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

        private async Task StoreConsumption(P1Meter p1Meter, P1Details p1Details, DateTime time, LiveWeer? liveWeer)
        {
            var consumptionList = new List<Consumption>();

            consumptionList.Add(new Consumption
            {
                Time = time,
                ConsumptionKWh = await CalculateConsumption(),
                // In case no weather data is present we store a large negative number.
                Humidity = liveWeer?.Luchtvochtigheid ?? -999, 
                Temperature = liveWeer?.Temp ?? -999,
                GlobalRadiation = liveWeer?.GlobalRadiation ?? -999
            });

            _consumptionService.AddRange(consumptionList);
        }

        public async Task<double> CalculateConsumption()
        {
            var solarPower = await _solarInverterManager.GetTotalACPowerInWatts();
            var netPower = await _p1MeterContainer.GetTotalPowerInWatts();
            var batteryPower = await _batteryContainer.GetTotalPowerInWatts();

            return solarPower + netPower + batteryPower;
        }
    }
}
