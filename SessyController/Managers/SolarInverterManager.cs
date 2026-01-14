using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Interfaces;

namespace SessyController.Managers
{
    public class SolarInverterManager : BackgroundService
    {
        private TimeZoneService _timeZoneService;
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private SettingsConfig _settingsConfig;
        private IOptionsMonitor<PowerSystemsConfig> _powerSystemConfigMonitor;

        private PowerSystemsConfig? _powerSystemsConfig { get; set; }
        private List<ISolarInverterService> _activeInverterServices { get; set; } = new();
        private double TotalCapacity => _activeInverterServices.Sum(serv => serv.Endpoints.Sum(ep => ep.Value.InverterMaxCapacity));

        public SolarInverterManager(IEnumerable<ISolarInverterService> inverterServices,
                                    TimeZoneService timezoneService,
                                    IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                    IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor)
        {
            _timeZoneService = timezoneService;
            _settingsConfigMonitor = settingsConfigMonitor;
            _settingsConfig = settingsConfigMonitor.CurrentValue;
            _settingsConfigMonitor.OnChange((config) =>
            {
                _settingsConfig = config;
            });

            _powerSystemConfigMonitor = powerSystemsConfigMonitor;
            UpdatePowersystemConfig(inverterServices, _powerSystemConfigMonitor.CurrentValue);
            _powerSystemConfigMonitor.OnChange((config) =>
            {
                UpdatePowersystemConfig(inverterServices, config);
            });
        }

        private void UpdatePowersystemConfig(IEnumerable<ISolarInverterService> inverterServices, PowerSystemsConfig config)
        {
            _powerSystemsConfig = config;
            FillActiveInverterServices(inverterServices);
        }

        private void FillActiveInverterServices(IEnumerable<ISolarInverterService> inverterServices)
        {
            _activeInverterServices = inverterServices
                .Where(inverterService => _powerSystemsConfig.Endpoints.ContainsKey(inverterService.ProviderName))
                .ToList();
        }

        public async Task<double> GetTotalACPowerInWatts()
        {
            double total = 0;

            if (_timeZoneService.GetSunlightLevel(_settingsConfig.Latitude, _settingsConfig.Longitude) == SolCalc.Data.SunlightLevel.Daylight)
            {
                foreach (var service in _activeInverterServices)
                {
                    total += await service.GetTotalACPowerInWatts();
                }
            }

            return total;
        }

        public ISolarInverterService? GetByName(string name) =>
            _activeInverterServices.FirstOrDefault(s => s.ProviderName.Equals(name, StringComparison.OrdinalIgnoreCase));

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _activeInverterServices)
            {
                await service.Start(cancellationToken);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _activeInverterServices)
            {
                await service.Stop(cancellationToken);
            }
        }

        private double? _lastWattsSet { get; set; } = null;

        public async Task ThrottleInverterToWatts(double watts)
        {
            if (TotalCapacity <= 0.0) throw new InvalidOperationException($"InverterMaxCapacity not set or wrong in config for one or more endpoints");

            if (!_lastWattsSet.HasValue || (_lastWattsSet.HasValue && _lastWattsSet != watts))
            {
                var percentage = (ushort)(watts / TotalCapacity * 100);

                if (percentage > 100)
                    percentage = 100;

                foreach (var service in _activeInverterServices)
                {
                    await service.ThrottleInverterToPercentage(percentage);
                }
            }

            _lastWattsSet = watts;
        }
    }
}
