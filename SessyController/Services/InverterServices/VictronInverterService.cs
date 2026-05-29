using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;

namespace SessyController.Services.InverterServices
{
    public class VictronInverterService : SunspecInverterService
    {
        public VictronInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      SettingsService settingsService,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Victron", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
