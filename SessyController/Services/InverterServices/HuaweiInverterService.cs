using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class HuaweiInverterService : SunspecInverterService
    {
        public HuaweiInverterService(LoggingService<SolarEdgeInverterService> logger,
                                     IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<SettingsConfig> settingsConfig,
                                     IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                     IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Huawei", httpClientFactory, settingsConfig, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
