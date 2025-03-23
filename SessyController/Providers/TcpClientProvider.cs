using Djohnnie.SolarEdge.ModBus.TCP;
using Microsoft.Extensions.Options;
using SessyController.Configurations;

namespace SessyController.Providers
{
    public class TcpClientProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PowerSystemsConfig _solarEdgeConfig;

        public TcpClientProvider(IServiceProvider serviceProvider, IOptions<PowerSystemsConfig> solarEdgeConfig)
        {
            _serviceProvider = serviceProvider;
            _solarEdgeConfig = solarEdgeConfig.Value;
        }

        public async Task<ModbusClient> GetModbusClient(string endpointName, string id)
        {
            if (!_solarEdgeConfig.Endpoints.TryGetValue(endpointName, out var ids))
                throw new InvalidOperationException($"No TcpClient configuration found for endpoint: {endpointName}");

            if(!ids.TryGetValue(id, out var config))
                throw new InvalidOperationException($"No TcpClient configuration fout for id {id}");

            if (config.IpAddress == null)
            {
                throw new InvalidOperationException($"No IP Address found for endpoint: {endpointName}");
            }

            var client = new ModbusClient(config.IpAddress, config.Port);

            await client.Connect();

            return client;
        }
    }
}
