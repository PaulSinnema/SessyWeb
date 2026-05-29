using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;

namespace SessyController.Services.InverterServices
{
    public class SolisInverterService : SunspecInverterService
    {
        public SolisInverterService(LoggingService<SolarEdgeInverterService> logger,
                                    IHttpClientFactory httpClientFactory,
                                    SettingsService settingsService,
                                    IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                    IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Solis", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
