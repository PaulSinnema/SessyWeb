using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;

namespace SessyController.Services.InverterServices
{
    public class HuaweiInverterService : SunspecInverterService
    {
        public HuaweiInverterService(LoggingService<SolarEdgeInverterService> logger,
                                     IHttpClientFactory httpClientFactory,
                                      SettingsService settingsService,
                                     IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                     IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Huawei", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
