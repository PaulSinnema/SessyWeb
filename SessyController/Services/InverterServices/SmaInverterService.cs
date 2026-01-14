using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class SmaInverterService : SunspecInverterService
    {
        public SmaInverterService(LoggingService<SolarEdgeInverterService> logger,
                                  IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<SettingsConfig> settingsConfig,
                                  IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                  IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Sma", httpClientFactory, settingsConfig, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
