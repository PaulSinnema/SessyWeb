using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class GoodWeInverterService : SunspecInverterService
    {
        public GoodWeInverterService(LoggingService<SolarEdgeInverterService> logger,
                                     IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<SettingsConfig> settingsConfig,
                                     IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                     IServiceScopeFactory serviceScopeFactory)
            : base(logger, "GoodWe", httpClientFactory, settingsConfig, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
