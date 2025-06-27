using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Interfaces;

namespace SessyController.Managers
{
    public class SolarInverterManager : BackgroundService
    {
        private PowerSystemsConfig _powerSystemsConfig { get; set; }
        private List<ISolarInverterService> _activeInverterServices { get; set; } = new();
        private double TotalCapacity => _activeInverterServices.Sum(serv => serv.Endpoints.Sum(ep => ep.Value.InverterMaxCapacity));

        public SolarInverterManager(IEnumerable<ISolarInverterService> inverterServices,
                                    IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig)
        {
            _powerSystemsConfig = powerSystemsConfig.CurrentValue;

            FillActiveInverterServices(inverterServices);

            powerSystemsConfig.OnChange((config) =>
            {
                _powerSystemsConfig = config;
                FillActiveInverterServices(inverterServices);
            });
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
            foreach (var service in _activeInverterServices)
            {
                total += await service.GetTotalACPowerInWatts();
            }

            return total;
        }

        public ISolarInverterService? GetByName(string name) =>
            _activeInverterServices.FirstOrDefault(s => s.ProviderName.Equals(name, StringComparison.OrdinalIgnoreCase));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            foreach (var service in _activeInverterServices)
            {
                await service.Start(stoppingToken);
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
