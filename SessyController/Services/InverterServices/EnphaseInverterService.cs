using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;
using SessyController.Interfaces;

namespace SessyController.Services.InverterServices
{
    public class EnphaseInverterService : SunspecInverterService
    {
        public EnphaseInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      SettingsService settingsService,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory) 
            : base(logger, "Enphase", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
