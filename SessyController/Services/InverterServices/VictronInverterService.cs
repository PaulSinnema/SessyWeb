using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class VictronInverterService : SunspecInverterService
    {
        public VictronInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<SettingsConfig> settingsConfig,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Victron", httpClientFactory, settingsConfig, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
