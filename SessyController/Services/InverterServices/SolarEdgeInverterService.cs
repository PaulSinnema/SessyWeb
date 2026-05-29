using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services;
using SessyCommon.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessyController.Services.InverterServices
{
    /// <summary>
    /// SolarEdge inverter service extending SunspecInverterService with a cloud API
    /// fallback for reading solar power when Modbus TCP is unavailable.
    ///
    /// Fallback uses the SolarEdge Monitoring API:
    ///   GET https://monitoringapi.solaredge.com/site/{siteId}/currentPowerFlow?api_key={apiKey}
    ///
    /// The response PV.currentPower field gives the current solar production in kW.
    /// Rate limit: 300 requests/day per API key — cached for CloudCacheSeconds to stay well within limits.
    /// </summary>
    public class SolarEdgeInverterService : SunspecInverterService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SolarEdgeCloudConfig _cloudConfig;
        private readonly LoggingService<SolarEdgeInverterService> _logger;
        private readonly TimeZoneService _timezoneService;

        // Cache fallback result to avoid exceeding the 300 requests/day limit.
        private double _cachedFallbackWatts = 0.0;
        private DateTime _cacheExpiry = DateTime.MinValue;
        private const int CloudCacheSeconds = 300; // 5 minutes — 288 requests/day max

        private const string CloudBaseUrl = "https://monitoringapi.solaredge.com";

        public SolarEdgeInverterService(LoggingService<SolarEdgeInverterService> logger,
                                        TimeZoneService timezoneService,
                                        IHttpClientFactory httpClientFactory,
                                        SettingsService settingsService,
                                        IOptionsMonitor<PowerSystemsConfig> powerSystemsConfig,
                                        IOptions<SolarEdgeCloudConfig> cloudConfig,
                                        IServiceScopeFactory serviceScopeFactory)
            : base(logger, "SolarEdge", httpClientFactory, settingsService, powerSystemsConfig, serviceScopeFactory)
        {
            _logger = logger;
            _timezoneService = timezoneService;
            _httpClientFactory = httpClientFactory;
            _cloudConfig = cloudConfig.Value;
        }

        /// <summary>
        /// SolarEdge supports the cloud API as fallback when Modbus TCP is down.
        /// Only active when SiteId and ApiKey are configured.
        /// </summary>
        public override bool SupportsFallback =>
            !string.IsNullOrEmpty(_cloudConfig.SiteId) &&
            !string.IsNullOrEmpty(_cloudConfig.ApiKey);

        /// <summary>
        /// Returns current solar power in Watts via the SolarEdge cloud API.
        /// Result is cached for CloudCacheSeconds to stay within the 300 requests/day limit.
        /// </summary>
        public override async Task<double> GetFallbackACPowerInWattsAsync()
        {
            if (!SupportsFallback)
                return 0.0;

            // Return cached value if still fresh.
            if (_timeZoneService.Now < _cacheExpiry)
                return _cachedFallbackWatts;

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(CloudBaseUrl);

                var url = $"/site/{_cloudConfig.SiteId}/currentPowerFlow?api_key={_cloudConfig.ApiKey}";
                var response = await client.GetAsync(url).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"SolarEdge cloud API returned {response.StatusCode} — using cached value.");
                    return _cachedFallbackWatts;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var powerFlow = JsonSerializer.Deserialize<SolarEdgePowerFlowResponse>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                // PV.currentPower is in kW — convert to Watts.
                double watts = (powerFlow?.SiteCurrentPowerFlow?.PV?.CurrentPower ?? 0.0) * 1000.0;

                _cachedFallbackWatts = watts;
                _cacheExpiry = _timeZoneService.Now.AddSeconds(CloudCacheSeconds);

                _logger.LogInformation($"SolarEdge cloud fallback: {watts:F0} W");

                return watts;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"SolarEdge cloud fallback failed: {ex.Message} — using cached value.");
                return _cachedFallbackWatts;
            }
        }

        // ── JSON response models ──────────────────────────────────────────────

        private class SolarEdgePowerFlowResponse
        {
            [JsonPropertyName("siteCurrentPowerFlow")]
            public PowerFlow? SiteCurrentPowerFlow { get; set; }
        }

        private class PowerFlow
        {
            [JsonPropertyName("PV")]
            public PowerSource? PV { get; set; }
        }

        private class PowerSource
        {
            [JsonPropertyName("status")]
            public string? Status { get; set; }

            // currentPower is in kW in the SolarEdge API response.
            [JsonPropertyName("currentPower")]
            public double CurrentPower { get; set; }
        }
    }
}