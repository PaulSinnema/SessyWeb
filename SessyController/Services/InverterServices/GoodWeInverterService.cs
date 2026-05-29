using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;

namespace SessyController.Services.InverterServices
{
    public class GoodWeInverterService : SunspecInverterService
    {
        public GoodWeInverterService(LoggingService<SolarEdgeInverterService> logger,
                                     IHttpClientFactory httpClientFactory,
                                      SettingsService settingsService,
                                     IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                     IServiceScopeFactory serviceScopeFactory)
            : base(logger, "GoodWe", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
