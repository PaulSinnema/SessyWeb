using Microsoft.Extensions.Options;
using SessyController.Configurations;

namespace SessyController.Services.InverterServices
{
    /// <summary>
    /// This class is used to read the result from the SolarEdge inverter.
    /// </summary>
    public class SolarEdgeInverterService : SunspecInverterService
    {
        public SolarEdgeInverterService(LoggingService<SolarEdgeInverterService> logger,
                                        IHttpClientFactory httpClientFactory,
                                        IOptions<PowerSystemsConfig> powerSystemsConfig,
                                        IServiceScopeFactory serviceScopeFactory)
            : base(logger, "SolarEdge", httpClientFactory, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}

