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
        }

        /// <summary>
        /// Gets the power consumed on a period in the week from history.
        /// </summary>
        public double GetPowerHistory(DateTime start, DateTime end, double temperature, double tempTolerance = 2)
        {
            // TODO: The data in the EnergyHistory is not enough to get the estimate of power required.

            if (start > end) throw new InvalidOperationException($"Start must be smaller than or equal to end. Start:{start} End:{end}");

            var dayStart = (int)start.DayOfWeek;
            var dayEnd = (int)end.DayOfWeek;
            var hourStart = start.Hour;
            var hourEnd = end.Hour;

            if (dayEnd == 6)
                dayEnd = 0;

            var data = _energyHistoryService.GetList((set) =>
            {
                return set
                    .Where(eh => (((int)eh.Time.DayOfWeek) == dayStart ||
                                  ((int)eh.Time.DayOfWeek) == dayEnd)
                                 //&&
                                 //eh.Time.Hour >= start.Hour &&
                                 //eh.Time.Hour <= end.Hour
                                 &&
                                 eh.Temperature > temperature - tempTolerance &&
                                 eh.Temperature < temperature + tempTolerance
                                 )
                    .OrderByDescending(eh => eh.Time)
                    .ToList();
            });

            if(data.Count > 0)
            {
                EnergyHistory? previousHistory = null;

                foreach (var eh in data)
                {
                    if(previousHistory != null)
                    {
                        if((previousHistory.Time - eh.Time).Hours <= 12)
                        {
                            var power1 = previousHistory.ConsumedTariff1 - eh.ConsumedTariff1;
                            var power2 = previousHistory.ConsumedTariff2 - eh.ConsumedTariff2;

                            return power1 + power2;
                        }
                    }

                    previousHistory = eh;
                }
            }

            return 0.0;
        }
    }
}
