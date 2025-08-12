using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services
{
    public class ConfigurationService
    {
        private PowerSystemsConfig? _powerSystemConfig { get; set; }

        public ConfigurationService(IOptions<PowerSystemsConfig> powerSystemsConfig)
        {
            _powerSystemConfig = powerSystemsConfig.Value;
        }

        public Dictionary<string, SessyCommon.Configurations.Endpoint> GetPowerSystemEndpoints(string endpointName)
        {
            if (!_powerSystemConfig.Endpoints.TryGetValue(endpointName, out var endpoints))
                throw new InvalidOperationException($"No TcpClient configuration found for endpoint: {endpointName}");

            return endpoints;
        }
    }
}
