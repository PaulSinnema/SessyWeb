using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;

namespace SessyController.Services.InverterServices
{
    public class SmaInverterService : SunspecInverterService
    {
        public SmaInverterService(LoggingService<SolarEdgeInverterService> logger,
                                  IHttpClientFactory httpClientFactory,
                                      SettingsService settingsService,
                                  IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                  IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Sma", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
