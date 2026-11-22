using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services;
using System.Net.Http.Headers;
using System.Text;

/// <summary>
/// API client for interacting with the P1 Meter.
/// </summary>
public class P1MeterService : IDisposable
{
    private LoggingService<P1MeterService> _logger { get; set; }

    private TimeZoneService _timeZoneService { get; set; }

    private IHttpClientFactory _httpClientFactory { get; set; }
    private IOptionsMonitor<SessyP1Config> _p1ConfigMonitor { get; set; }
    private SessyP1Config _p1Configuration { get; set; }

    private ControlModeService _controlMode { get; set; }

    private IDisposable? _p1ConfigSubscription { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="P1MeterService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating HTTP clients.</param>
    public P1MeterService(LoggingService<P1MeterService> logger,
                          TimeZoneService timeZoneService,
                          IHttpClientFactory httpClientFactory,
                          IOptionsMonitor<SessyP1Config> p1ConfigMonitor,
                          ControlModeService controlMode)
    {
        _logger = logger;
        _timeZoneService = timeZoneService;
        _httpClientFactory = httpClientFactory;
        _p1ConfigMonitor = p1ConfigMonitor;
        _controlMode = controlMode;
        _p1Configuration = _p1ConfigMonitor.CurrentValue;

        _p1ConfigSubscription = _p1ConfigMonitor.OnChange((settings) => _p1Configuration = settings);
    }

    /// <summary>
    /// Returns endpoint information for the P1 meter.
    /// </summary>
    /// <param name="id">Id of the P1 meteer in the Appsettings.json.</param>
    private SessyP1Endpoint GetP1Endpoint(string id)
    {
        if(!_p1Configuration.Endpoints.TryGetValue(id, out var config))
        {
            throw new InvalidOperationException($"No P1 configuration found for {id}");
        }

        if(config == null)
        {
            throw new InvalidOperationException($"P1 configuratioin is empty for {id}");
        }

        return config;
    }

    /// <summary>
    /// Creates a configured HTTP client for interacting with the P1 Meter API.
    /// </summary>
    /// <param name="id">Id of the P1 meteer in the Appsettings.json.</param>
    /// <returns>A configured <see cref="HttpClient"/> instance.</returns>
    private HttpClient CreateHttpClient(string id)
        => SessyHttp.CreateClient(_httpClientFactory, GetP1Endpoint(id), id, requireCredentials: true);

    /// <summary>
    /// Retrieves the details of the P1 Meter.
    /// </summary>
    /// <param name="baseAddress">The base address of the P1 Meter API.</param>
    /// <param name="username">The username for authentication.</param>
    /// <param name="password">The password for authentication.</param>
    /// <returns>A <see cref="P1Details"/> object representing the details of the P1 Meter.</returns>
    public async Task<P1Details?> GetP1DetailsAsync(string id)
    {
        using var client = CreateHttpClient(id);
        return await client.GetJsonAsync<P1Details>("/api/v2/p1/details").ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the current grid target from the P1 Meter.
    /// </summary>
    /// <param name="baseAddress">The base address of the P1 Meter API.</param>
    /// <param name="username">The username for authentication.</param>
    /// <param name="password">The password for authentication.</param>
    /// <returns>A <see cref="GridTargetGet"/> object representing the current grid target.</returns>
    public async Task<GridTargetGet?> GetGridTargetAsync(string id)
    {
        using var client = CreateHttpClient(id);
        return await client.GetJsonAsync<GridTargetGet>("/api/v1/meter/grid_target").ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a new grid target on the P1 Meter.
    /// </summary>
    /// <param name="baseAddress">The base address of the P1 Meter API.</param>
    /// <param name="username">The username for authentication.</param>
    /// <param name="password">The password for authentication.</param>
    /// <param name="gridTarget">The new grid target to set.</param>
    /// <returns>An awaitable task representing the asynchronous operation.</returns>
    public async Task SetGridTargetAsync(string id, GridTargetPost gridTarget)
    {
        // Same guard as SessyService.SetPowerSetpointAsync — do not drive when Charged or a provider is in control.
        if (_controlMode.RefusesWrite(_logger, $"SetGridTarget({id}, {gridTarget.GridTarget})"))
            return;

        _logger.LogInformation($"SetGridTarget({id}, {gridTarget.GridTarget})");

        using var client = CreateHttpClient(id);
        await client.PostJsonAsync("/api/v1/meter/grid_target", gridTarget).ConfigureAwait(false);
    }

    private bool isDisposed = false;

    public void Dispose()
    {
        if (!isDisposed)
        {
            _p1ConfigSubscription.Dispose();
            isDisposed = true;
        }

    }

    /// <summary>
    /// Represents detailed information retrieved from the P1 Meter.
    /// </summary>
    public class P1Details
    {
        /// <summary>
        /// Status of the P1 Meter (e.g., "ok").
        /// </summary>
        [JsonProperty("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Current state of the P1 Meter.
        /// </summary>
        [JsonProperty("state")]
        public string? State { get; set; }

        /// <summary>
        /// DSMR version used by the meter.
        /// </summary>
        [JsonProperty("dsmr_version")]
        public int DsmrVersion { get; set; }

        /// <summary>
        /// Power consumption for tariff 1 in watts.
        /// </summary>
        [JsonProperty("power_consumed_tariff1")]
        public long PowerConsumedTariff1 { get; set; }

        /// <summary>
        /// Power production for tariff 1 in watts.
        /// </summary>
        [JsonProperty("power_produced_tariff1")]
        public long PowerProducedTariff1 { get; set; }

        /// <summary>
        /// Power consumption for tariff 2 in watts.
        /// </summary>
        [JsonProperty("power_consumed_tariff2")]
        public long PowerConsumedTariff2 { get; set; }

        /// <summary>
        /// Power production for tariff 2 in watts.
        /// </summary>
        [JsonProperty("power_produced_tariff2")]
        public long PowerProducedTariff2 { get; set; }

        /// <summary>
        /// The current tariff indicator (e.g., 1 or 2).
        /// </summary>
        [JsonProperty("tariff_indicator")]
        public int TariffIndicator { get; set; }

        /// <summary>
        /// Current power consumption in watts.
        /// </summary>
        [JsonProperty("power_consumed")]
        public int PowerConsumed { get; set; }

        /// <summary>
        /// Current power production in watts.
        /// </summary>
        [JsonProperty("power_produced")]
        public int PowerProduced { get; set; }

        /// <summary>
        /// Total power in watts.
        /// </summary>
        [JsonProperty("power_total")]
        public int PowerTotal { get; set; }

        // Add additional fields similarly with appropriate JsonProperty attributes.
    }

    /// <summary>
    /// Represents the current grid target settings of the P1 Meter.
    /// </summary>
    public class GridTargetGet
    {
        /// <summary>
        /// The current grid target value in watts.
        /// </summary>
        [JsonProperty("grid_target")]
        public int GridTarget { get; set; }
    }

    /// <summary>
    /// Represents the payload for setting a new grid target on the P1 Meter.
    /// </summary>
    public class GridTargetPost
    {
        /// <summary>
        /// The desired grid target value in watts.
        /// </summary>
        [JsonProperty("grid_target")]
        public int GridTarget { get; set; }
    }
}
