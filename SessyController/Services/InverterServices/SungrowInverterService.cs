using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;

namespace SessyController.Services.InverterServices
{
    public class SungrowInverterService : SunspecInverterService
    {
        public SungrowInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      SettingsService settingsService,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Sungrow", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
