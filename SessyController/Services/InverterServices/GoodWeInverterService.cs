using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class GoodWeInverterService : SunspecInverterService
    {
        public GoodWeInverterService(LoggingService<SolarEdgeInverterService> logger,
                                     IHttpClientFactory httpClientFactory,
                                     IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                     IServiceScopeFactory serviceScopeFactory)
            : base(logger, "GoodWe", httpClientFactory, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
