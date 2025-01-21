using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SessyController.Configurations;

namespace SessyController.Services
{
    /// <summary>
    /// This class is used to communicatie with a Sessy battery.
    /// </summary>
    public class SessyService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SessyBatteryConfig _batteryConfig;
        private readonly LoggingService<SessyService> _logger;

        public SessyService(LoggingService<SessyService> logger,
                            IHttpClientFactory httpClientFactory,
                            IOptions<SessyBatteryConfig> batteryConfig)
        {
            _httpClientFactory = httpClientFactory;
            _batteryConfig = batteryConfig.Value;
            _logger = logger; ;
        }

        private HttpClient CreateHttpClient(SessyBatteryEndpoint battery)
        {
            if (battery == null) throw new ArgumentNullException(nameof(battery));
            if (string.IsNullOrWhiteSpace(battery.BaseUrl)) throw new ArgumentNullException(nameof(battery.BaseUrl));

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(battery.BaseUrl);
            var authToken = Encoding.ASCII.GetBytes($"{battery.UserId}:{battery.Password}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authToken));
            return client;
        }

        /// <summary>
        /// Retrieves the current power status of the battery, including charge state, power metrics, and phase details.
        /// </summary>
        /// <param name="id">Id of the battery configuration object containing authentication and URL details.</param>
        /// <returns>
        /// A <see cref="PowerStatus"/> object representing the current power status of the battery.
        /// </returns>
        /// <exception cref="HttpRequestException">
        /// Thrown when the API request fails or the response status code is not successful.
        /// </exception>
        public async Task<PowerStatus?> GetPowerStatusAsync(string id)
        {
            _logger.LogInformation($"GetPowerStatusAsync({id})");

            SessyBatteryEndpoint battery = GetBatteryConfiguration(id);
            using var client = CreateHttpClient(battery);
            var response = await client.GetAsync("/api/v1/power/status").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<PowerStatus>(content);
        }

        /// <summary>
        /// Retrieves the currently active power strategy of the battery.
        /// </summary>
        /// <param name="id">Id of the battery configuration object containing authentication and URL details.</param>
        /// <returns>
        /// An <see cref="ActivePowerStrategy"/> object representing the currently active power strategy of the battery.
        /// </returns>
        /// <exception cref="HttpRequestException">
        /// Thrown when the API request fails or the response status code is not successful.
        /// </exception>
        public async Task<ActivePowerStrategy?> GetActivePowerStrategyAsync(string id)
        {
            _logger.LogInformation($"GetActivePowerStrategyAsync({id})");

            SessyBatteryEndpoint battery = GetBatteryConfiguration(id);
            using var client = CreateHttpClient(battery);
            var response = await client.GetAsync("/api/v1/power/active_strategy");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<ActivePowerStrategy>(content);
        }

        /// <summary>
        /// Sets the active power strategy for the battery.
        /// </summary>
        /// <param name="id">Id of the battery configuration object containing authentication and URL details.</param>
        /// <param name="strategy">The strategy to be applied.</param>
        /// <returns>An awaitable Task representing the asynchronous operation.</returns>
        public async Task SetActivePowerStrategyAsync(string id, ActivePowerStrategy strategy)
        {
            _logger.LogInformation($"SetActivePowerStrategyAsync({id}, {strategy.Strategy})");

            SessyBatteryEndpoint battery = GetBatteryConfiguration(id);
            using var client = CreateHttpClient(battery);
            var json = JsonConvert.SerializeObject(strategy);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/v1/power/active_strategy", content);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Sets the power setpoint for the battery.
        /// </summary>
        /// <param name="id">Id of the battery configuration object containing authentication and URL details.</param>
        /// <param name="setpoint">The desired power setpoint in watts.</param>
        /// <returns>An awaitable Task representing the asynchronous operation.</returns>
        public async Task SetPowerSetpointAsync(string id, PowerSetpoint setpoint)
        {
            _logger.LogInformation($"SetPowerSetpoint({id}, {setpoint})");

            SessyBatteryEndpoint battery = GetBatteryConfiguration(id);
            using var client = CreateHttpClient(battery);
            var json = JsonConvert.SerializeObject(setpoint);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/v1/power/setpoint", content);
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Get the configuration for a battery with Id.
        /// </summary>
        /// <param name="id">>Id of the battery configuration object containing authentication and URL details.</param>
        /// <returns>SessyBatteryEndpoint</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the configuration can not be found in Appsettings.json.
        /// </exception>
        private SessyBatteryEndpoint GetBatteryConfiguration(string id)
        {
            if (!_batteryConfig.Batteries.TryGetValue(id, out var battery))
            {
                throw new InvalidOperationException($"Battery with ID {id} not found.");
            }

            return battery;
        }
    }


    /// <summary>
    /// Represents the overall power status of the battery, including charge state, frequency, and phase information.
    /// </summary>
    public class PowerStatus
    {
        /// <summary>
        /// The status of the request (e.g., "ok").
        /// </summary>
        [JsonProperty("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Detailed information about the Sessy battery's current state.
        /// </summary>
        [JsonProperty("sessy")]
        public Sessy? Sessy { get; set; }

        /// <summary>
        /// Information about renewable energy for phase 1.
        /// </summary>
        [JsonProperty("renewable_energy_phase1")]
        public Phase? RenewableEnergyPhase1 { get; set; }

        /// <summary>
        /// Information about renewable energy for phase 2.
        /// </summary>
        [JsonProperty("renewable_energy_phase2")]
        public Phase? RenewableEnergyPhase2 { get; set; }

        /// <summary>
        /// Information about renewable energy for phase 3.
        /// </summary>
        [JsonProperty("renewable_energy_phase3")]
        public Phase? RenewableEnergyPhase3 { get; set; }
    }

    /// <summary>
    /// Represents detailed information about the Sessy battery's state.
    /// </summary>
    public class Sessy
    {
        /// <summary>
        /// The current state of charge as a fraction (e.g., 0.9 for 90%).
        /// </summary>
        [JsonProperty("state_of_charge")]
        public double StateOfCharge { get; set; }

        /// <summary>
        /// The current power output in watts.
        /// </summary>
        [JsonProperty("power")]
        public int Power { get; set; }

        /// <summary>
        /// The power setpoint in watts.
        /// </summary>
        [JsonProperty("power_setpoint")]
        public int PowerSetpoint { get; set; }

        /// <summary>
        /// The current system state (e.g., "SYSTEM_STATE_STANDBY").
        /// </summary>
        [JsonProperty("system_state")]
        public string? SystemState { get; set; }

        /// <summary>
        /// Detailed information about the current system state.
        /// </summary>
        [JsonProperty("system_state_details")]
        public string? SystemStateDetails { get; set; }

        /// <summary>
        /// The frequency in millihertz (e.g., 49985 mHz for 49.985 Hz).
        /// </summary>
        [JsonProperty("frequency")]
        public int Frequency { get; set; }

        /// <summary>
        /// The current from the inverter in milliamps.
        /// </summary>
        [JsonProperty("inverter_current_ma")]
        public int InverterCurrentMa { get; set; }
    }

    /// <summary>
    /// Represents detailed information about a single electrical phase, including voltage, current, and power.
    /// </summary>
    public class Phase
    {
        /// <summary>
        /// The root mean square (RMS) voltage in millivolts.
        /// </summary>
        [JsonProperty("voltage_rms")]
        public int VoltageRms { get; set; }

        /// <summary>
        /// The root mean square (RMS) current in milliamps.
        /// </summary>
        [JsonProperty("current_rms")]
        public int CurrentRms { get; set; }

        /// <summary>
        /// The current power output for this phase in watts.
        /// </summary>
        [JsonProperty("power")]
        public int Power { get; set; }
    }

    /// <summary>
    /// Represents the active power strategy used by the battery.
    /// </summary>
    public class ActivePowerStrategy
    {
        public enum PowerStrategies
        {
            POWER_STRATEGY_NOM,
            POWER_STRATEGY_ROI,
            POWER_STRATEGY_API,
            POWER_STRATEGY_IDLE,
            POWER_STRATEGY_SESSY_CONNECT,
            POWER_STRATEGY_ECO
        };

        [JsonProperty("strategy")]
        public string? Strategy { get; set; }

        [JsonProperty("status")]
        public string? Status { get; set; }
    }

    /// <summary>
    /// Represents the power setpoint for the battery.
    /// </summary>
    public class PowerSetpoint
    {
        [JsonProperty("setpoint")]
        public int Setpoint { get; set; }
    }
}