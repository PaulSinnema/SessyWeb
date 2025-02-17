using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SessyController.Configurations;

/// <summary>
/// API client for interacting with the P1 Meter.
/// </summary>
public class P1MeterService
{
    private IHttpClientFactory _httpClientFactory { get; set; }
    private IOptionsMonitor<SessyP1Config> _p1ConfigMonitor { get; set; }
    private SessyP1Config _p1Configuration { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="P1MeterService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating HTTP clients.</param>
    public P1MeterService(IHttpClientFactory httpClientFactory, IOptionsMonitor<SessyP1Config> p1ConfigMonitor)
    {
        _httpClientFactory = httpClientFactory;
        _p1ConfigMonitor = p1ConfigMonitor;
        _p1Configuration = _p1ConfigMonitor.CurrentValue;

        _p1ConfigMonitor.OnChange((settings) => _p1Configuration = settings);
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
    {
        var endpoint = GetP1Endpoint(id);

        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl)) throw new InvalidOperationException($"Baseurl for P1 configuration with id {id} is empty");
        if (string.IsNullOrWhiteSpace(endpoint.UserId)) throw new InvalidOperationException($"UserId for P1 configuration with id {id} is empty");
        if (string.IsNullOrWhiteSpace(endpoint.Password)) throw new InvalidOperationException($"Password for P1 configuration with id {id} is empty");

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(endpoint.BaseUrl);
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{endpoint.UserId}:{endpoint.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        return client;
    }

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
        var response = await client.GetAsync("/api/v2/p1/details");

        // Ensure the response is successful
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<P1Details>(content);
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
        var response = await client.GetAsync("/api/v1/meter/grid_target");

        // Ensure the response is successful
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<GridTargetGet>(content);
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
        using var client = CreateHttpClient(id);
        var json = JsonConvert.SerializeObject(gridTarget);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/meter/grid_target", content);

        // Ensure the response is successful
        response.EnsureSuccessStatusCode();
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
