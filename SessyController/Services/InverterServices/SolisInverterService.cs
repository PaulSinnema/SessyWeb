using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class SolisInverterService : SunspecInverterService
    {
        public SolisInverterService(LoggingService<SolarEdgeInverterService> logger,
                                    IHttpClientFactory httpClientFactory,
                                    IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                    IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Solis", httpClientFactory, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
