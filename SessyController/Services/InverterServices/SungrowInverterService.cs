using Microsoft.Extensions.Options;
using SessyController.Configurations;

namespace SessyController.Services.InverterServices
{
    public class SungrowInverterService : SunspecInverterService
    {
        public SungrowInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Sungrow", httpClientFactory, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
