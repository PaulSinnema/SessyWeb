using Microsoft.Extensions.Options;
using SessyController.Configurations;

namespace SessyController.Services.InverterServices
{
    public class GoodWeInverterService : SunspecInverterService
    {
        public GoodWeInverterService(LoggingService<SolarEdgeInverterService> logger,
                                     IHttpClientFactory httpClientFactory,
                                     IOptions<PowerSystemsConfig> powerSystemsConfig,
                                     IServiceScopeFactory serviceScopeFactory)
            : base(logger, "GoodWe", httpClientFactory, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
