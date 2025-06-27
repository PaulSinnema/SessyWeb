using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Interfaces;

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
