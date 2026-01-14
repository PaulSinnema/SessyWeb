using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Interfaces;

namespace SessyController.Services.InverterServices
{
    public class EnphaseInverterService : SunspecInverterService
    {
        public EnphaseInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<SettingsConfig> settingsConfig,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory) 
            : base(logger, "Enphase", httpClientFactory, settingsConfig, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
