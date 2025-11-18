using Djohnnie.SolarEdge.ModBus.TCP;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;

namespace SessyController.Providers
{
    public class TcpClientProvider
    {
        private PowerSystemsConfig _solarEdgeConfig { get; set; }

        public TcpClientProvider(IOptionsMonitor<PowerSystemsConfig> solarEdgeConfig)
        {
            _solarEdgeConfig = solarEdgeConfig.CurrentValue;

            solarEdgeConfig.OnChange((PowerSystemsConfig config) => _solarEdgeConfig = config);
        }

        public async Task<ModbusClient> GetModbusClient(string endpointName, string id)
        {
            try
            {
                if (!_solarEdgeConfig.Endpoints.TryGetValue(endpointName, out var ids))
                    throw new InvalidOperationException($"No TcpClient configuration found for endpoint: {endpointName}");

                if (!ids.TryGetValue(id, out var config))
                    throw new InvalidOperationException($"No TcpClient configuration found for id {id}");

                if (config.IpAddress == null)
                {
                    throw new InvalidOperationException($"No IP Address found for endpoint: {endpointName}");
                }

                var client = new ModbusClient(config.IpAddress, config.Port);

                await client.Connect();

                return client;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not connect to client {ex.ToDetailedString()}");
            }
        }
    }
}
