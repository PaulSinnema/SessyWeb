using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class PowerEstimatesService
    {
        private SettingsConfig _settingsConfig { get; set; }
        private SessyBatteryConfig _sessyBatteryConfig { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }
        private EnergyHistoryService _energyHistoryService { get; set; }

        private IServiceScope _scope { get; set; }
        private SessyService _sessyService { get; set; }
        private P1MeterService _p1MeterService { get; set; }
        private DayAheadMarketService _dayAheadMarketService { get; set; }
        private SolarEdgeService _solarEdgeService { get; set; }
        private SolarService _solarService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private WeatherService _weatherService { get; set; }

        public PowerEstimatesService(LoggingService<BatteriesService> logger,
                                IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
                                EnergyHistoryService energyHistoryService,
                                IServiceScopeFactory serviceScopeFactory)
        {
            _settingsConfig = settingsConfigMonitor.CurrentValue;
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue;
            _serviceScopeFactory = serviceScopeFactory;
            _energyHistoryService = energyHistoryService;

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
            _weatherService = _scope.ServiceProvider.GetRequiredService<WeatherService>();
        }

        /// <summary>
        /// Gets the power consumed on a period in the week from history.
        /// </summary>
        public double GetPowerEstimate(DateTime date, double temperature, double tempTolerance = 2)
        {
            var tempFrom = temperature - tempTolerance;
            var tempTo = temperature + tempTolerance;
            var dayOfWeek = date.DayOfWeek;

            var histories = _energyHistoryService.GetList((set) =>
            {
                return set
                    .Where(eh => eh.Temperature >= tempFrom &&
                                 eh.Temperature <= tempTo &&
                                 eh.Time.DayOfWeek == dayOfWeek)
                    .OrderByDescending(eh => eh.Time)
                    .ToList();
            });

            foreach (var history in histories)
            {
                var previous = _energyHistoryService.Get((set) =>
                {
                    return set
                        .Where(eh => eh.Time == history.Time.AddHours(-1))
                        .SingleOrDefault();
                });

                if(previous != null)
                {
                    var gridPower = new GridPower(history, previous);

                    return gridPower.TotalInversed; 
                }
            }

            return 0.0;
        }
    }
}
