using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class SmaInverterService : SunspecInverterService
    {
        public SmaInverterService(LoggingService<SolarEdgeInverterService> logger,
                                  IHttpClientFactory httpClientFactory,
                                  IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                  IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Sma", httpClientFactory, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
