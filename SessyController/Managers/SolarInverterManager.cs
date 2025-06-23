using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Interfaces;

namespace SessyController.Managers
{
    public class SolarInverterManager : BackgroundService
    {
        private PowerSystemsConfig _powerSystemsConfig { get; set; }
        private List<ISolarInverterService> _activeInverters { get; set; } = new();

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
            _activeInverters = inverterServices
                .Where(inverterService => _powerSystemsConfig.Endpoints.ContainsKey(inverterService.ProviderName))
                .ToList();
        }

        public async Task<double> GetTotalACPowerInWatts()
        {
            double total = 0;
            foreach (var service in _activeInverters)
            {
                total += await service.GetTotalACPowerInWatts();
            }

            return total;
        }

        public ISolarInverterService? GetByName(string name) =>
            _activeInverters.FirstOrDefault(s => s.ProviderName.Equals(name, StringComparison.OrdinalIgnoreCase));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            foreach (var service in _activeInverters)
            {
                await service.Start(stoppingToken);
            }
        }
    }
}
