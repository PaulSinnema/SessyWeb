using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class SungrowInverterService : SunspecInverterService
    {
        public SungrowInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<SettingsConfig> settingsConfig,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Sungrow", httpClientFactory, settingsConfig, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
