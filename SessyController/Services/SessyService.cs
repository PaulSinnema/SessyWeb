﻿using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SessyController.Configurations;
using SessyController.Services.Items;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;

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
            _logger.LogWarning($"SetPowerSetpoint({id}, {setpoint.Setpoint})");

            SessyBatteryEndpoint battery = GetBatteryConfiguration(id);
            using var client = CreateHttpClient(battery);
            var json = JsonConvert.SerializeObject(setpoint);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/v1/power/setpoint", content);
            response.EnsureSuccessStatusCode();
        }

        public async Task<SessyScheduleResponse?> GetDynamicScheduleAsync(string id)
        {
            _logger.LogInformation($"GetDynamicScheduleAsync({id})");

            SessyBatteryEndpoint battery = GetBatteryConfiguration(id);
            using var client = CreateHttpClient(battery);
            var response = await client.GetAsync("/api/v2/dynamic/schedule");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<SessyScheduleResponse>(content);
            return result;
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

    public class SessyScheduleResponse
    {
        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("dynamic_schedule")]
        public List<DynamicScheduleItem>? DynamicSchedule { get; set; }

        [JsonProperty("energy_prices")]
        public List<EnergyPriceItem>? EnergyPrices { get; set; }
    }

    public class DynamicScheduleItem
    {
        [JsonProperty("start_time")]
        public long StartTimeUnix { get; set; }

        [JsonProperty("end_time")]
        public long EndTimeUnix { get; set; }

        [JsonProperty("power")]
        public int Power { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public DateTime StartTime => DateTimeOffset.FromUnixTimeSeconds(StartTimeUnix).DateTime;

        [Newtonsoft.Json.JsonIgnore]
        public DateTime EndTime => DateTimeOffset.FromUnixTimeSeconds(EndTimeUnix).DateTime;
    }

    public class EnergyPriceItem
    {
        [JsonProperty("start_time")]
        public long StartTimeUnix { get; set; }

        [JsonProperty("end_time")]
        public long EndTimeUnix { get; set; }

        [JsonProperty("price")]
        public int Price { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public DateTime StartTime => DateTimeOffset.FromUnixTimeSeconds(StartTimeUnix).DateTime;

        [Newtonsoft.Json.JsonIgnore]
        public DateTime EndTime => DateTimeOffset.FromUnixTimeSeconds(EndTimeUnix).DateTime;
    }

    public class PowerStrategy
    {
        [JsonProperty("date")]
        public string? Date { get; set; }

        [JsonProperty("power")]
        public List<int>? Power { get; set; }
    }

    public class EnergyPrices
    {
        [JsonProperty("date")]
        public string? Date { get; set; }

        [JsonProperty("price")]
        public List<int>? Price { get; set; }
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
        /// State of charge in %
        /// </summary>
        public double StateOfCharge100
        {
            get
            {
                return Math.Round(StateOfCharge * 100.0, 1);
            }
        }

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
        /// System states
        /// </summary>
        public enum SystemStates
        {
            SYSTEM_STATE_INIT = 0,
            SYSTEM_STATE_WAIT_FOR_PERIPHERALS = 1,
            SYSTEM_STATE_STANDBY = 2,
            SYSTEM_STATE_WAITING_FOR_SAFE_SITUATION = 3,
            SYSTEM_STATE_WAITING_IN_SAFE_SITUATION = 4,
            SYSTEM_STATE_RUNNING_SAFE = 5,
            SYSTEM_STATE_OVERRIDE_OVERFREQUENCY = 6,
            SYSTEM_STATE_OVERRIDE_UNDERFREQUENCY = 7,
            SYSTEM_STATE_DISCONNECT = 8,
            SYSTEM_STATE_RECONNECT = 9,
            SYSTEM_STATE_ERROR = 10,
            SYSTEM_STATE_BATTERY_FULL = 11,
            SYSTEM_STATE_BATTERY_EMPTY = 12,
            SYSTEM_STATE_OVERRIDE_BATTERY_UNDERVOLTAGE = 13,
        };

        private string _systemStateString { get; set; } = string.Empty;

        private SystemStates _systemState { get; set; }

        /// <summary>
        /// The current system state (e.g., "SYSTEM_STATE_STANDBY").
        /// </summary>
        // [JsonProperty("system_state")]
        public SystemStates SystemState
        {
            get
            {
                var names = Enum.GetNames(typeof(SystemStates));
                return (SystemStates)Enum.Parse(typeof(SystemStates), SystemStateString);
            }
        }

        [JsonProperty("system_state")]
        public string SystemStateString
        {
            get
            {
                return _systemStateString;
            }
            set
            {
                _systemStateString = value;

                GetSystemState();
            }
        }

        private void GetSystemState()
        {
            var names = Enum.GetNames(typeof(SystemStates));
            _systemState = (SystemStates)Enum.Parse(typeof(SystemStates), _systemStateString);
        }

        public string SystemStateColor
        {
            get
            {
                var color = "green";

                switch (SystemState)
                {
                    case SystemStates.SYSTEM_STATE_INIT:
                        color = "magenta";
                        break;

                    case SystemStates.SYSTEM_STATE_STANDBY:
                        color = "gray";
                        break;

                    case SystemStates.SYSTEM_STATE_WAITING_FOR_SAFE_SITUATION:
                    case SystemStates.SYSTEM_STATE_WAITING_IN_SAFE_SITUATION:
                        color = "orange";
                        break;

                    case SystemStates.SYSTEM_STATE_RUNNING_SAFE:
                        break;

                    case SystemStates.SYSTEM_STATE_WAIT_FOR_PERIPHERALS:
                    case SystemStates.SYSTEM_STATE_OVERRIDE_OVERFREQUENCY:
                    case SystemStates.SYSTEM_STATE_OVERRIDE_UNDERFREQUENCY:
                    case SystemStates.SYSTEM_STATE_DISCONNECT:
                    case SystemStates.SYSTEM_STATE_ERROR:
                        color = "red";
                        break;

                    case SystemStates.SYSTEM_STATE_BATTERY_FULL:
                        color = "blue";
                        break;

                    case SystemStates.SYSTEM_STATE_BATTERY_EMPTY:
                        color = "lightblue";
                        break;

                    case SystemStates.SYSTEM_STATE_RECONNECT:
                    case SystemStates.SYSTEM_STATE_OVERRIDE_BATTERY_UNDERVOLTAGE:
                        color = "purple";

                        break;

                    default:
                        break;
                }

                return color;
            }
        }

        public string SystemStateTitle
        {
            get
            {
                var title = "?????";

                switch (SystemState)
                {
                    case SystemStates.SYSTEM_STATE_INIT:
                        title = "Initializing";
                        break;

                    case SystemStates.SYSTEM_STATE_STANDBY:
                        title = "Standby";
                        break;

                    case SystemStates.SYSTEM_STATE_WAITING_FOR_SAFE_SITUATION:
                        title = "Waiting for safe situation";
                        break;

                    case SystemStates.SYSTEM_STATE_WAITING_IN_SAFE_SITUATION:
                        title = "Waiting in safe situation";
                        break;

                    case SystemStates.SYSTEM_STATE_RUNNING_SAFE:
                        title = "Running safe";
                        break;

                    case SystemStates.SYSTEM_STATE_WAIT_FOR_PERIPHERALS:
                        title = "Wating for peripherals";
                        break;

                    case SystemStates.SYSTEM_STATE_OVERRIDE_OVERFREQUENCY:
                        title = "Override over frequency";
                        break;

                    case SystemStates.SYSTEM_STATE_OVERRIDE_UNDERFREQUENCY:
                        title = "Override under frequency";
                        break;

                    case SystemStates.SYSTEM_STATE_DISCONNECT:
                        title = "Disconnected";
                        break;

                    case SystemStates.SYSTEM_STATE_ERROR:
                        title = "Error";
                        break;

                    case SystemStates.SYSTEM_STATE_BATTERY_FULL:
                        title = "Full";
                        break;

                    case SystemStates.SYSTEM_STATE_BATTERY_EMPTY:
                        title = "Empty";
                        break;

                    case SystemStates.SYSTEM_STATE_RECONNECT:
                        title = "Reconnecting";
                        break;

                    case SystemStates.SYSTEM_STATE_OVERRIDE_BATTERY_UNDERVOLTAGE:
                        title = "Override under voltage";

                        break;

                    default:
                        break;
                }

                return title;
            }
        }

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

        [JsonProperty("strategy_overridden")]
        public bool StrategyOverridden { get; set; }
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

        public PowerStrategies PowerStrategy => (PowerStrategies)Enum.Parse(typeof(PowerStrategies), Strategy!);

        public string StrategyVisual
        {
            get
            {
                switch (PowerStrategy)
                {
                    case PowerStrategies.POWER_STRATEGY_NOM:
                        return "Zero Net Home";
                    case PowerStrategies.POWER_STRATEGY_ROI:
                        return "Dynamic";
                    case PowerStrategies.POWER_STRATEGY_API:
                        return "Sessy Web (Open API)";
                    case PowerStrategies.POWER_STRATEGY_IDLE:
                        return "Idle";
                    case PowerStrategies.POWER_STRATEGY_SESSY_CONNECT:
                        return "Sessy connect";
                    case PowerStrategies.POWER_STRATEGY_ECO:
                        return "Eco mode";
                    default:
                        return "Unknown";
                }
            }
        }

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