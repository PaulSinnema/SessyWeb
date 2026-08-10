using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services
{
    public class ConfigurationService
    {
        // Monitor rather than IOptions, so an edit to appsettings.json lands without a restart.
        private readonly IOptionsMonitor<PowerSystemsConfig> _powerSystemConfigMonitor;

        private PowerSystemsConfig _powerSystemConfig => _powerSystemConfigMonitor.CurrentValue;

        public ConfigurationService(IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor)
        {
            _powerSystemConfigMonitor = powerSystemsConfigMonitor;
        }

        public Dictionary<string, SessyCommon.Configurations.Endpoint> GetPowerSystemEndpoints(string endpointName)
        {
            if (!_powerSystemConfig.Endpoints.TryGetValue(endpointName, out var endpoints))
                throw new InvalidOperationException($"No TcpClient configuration found for endpoint: {endpointName}");

            return endpoints;
        }
    }
}
