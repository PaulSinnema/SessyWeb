using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services.InverterServices
{
    public class SolisInverterService : SunspecInverterService
    {
        public SolisInverterService(LoggingService<SolarEdgeInverterService> logger,
                                    IHttpClientFactory httpClientFactory,
                                    IOptionsMonitor<SettingsConfig> settingsConfig,
                                    IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                    IServiceScopeFactory serviceScopeFactory)
            : base(logger, "Solis", httpClientFactory, settingsConfig, powerSystemsConfig, serviceScopeFactory)
        {
        }
    }
}
