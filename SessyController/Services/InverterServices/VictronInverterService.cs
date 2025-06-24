using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Interfaces;

namespace SessyController.Services.InverterServices
{
    public class VictronInverterService : SunspecInverterService
    {
        public VictronInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      IOptions<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Victron", httpClientFactory, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
