using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Services;

namespace SessyController.Managers
{
    public class SolarInverterManager : BackgroundService
    {
        private readonly LoggingService<SolarInverterManager> _logger;
        private TimeZoneService _timeZoneService;
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private SettingsConfig _settingsConfig;
        private IOptionsMonitor<PowerSystemsConfig> _powerSystemConfigMonitor;

        private PowerSystemsConfig? _powerSystemsConfig { get; set; }
        private List<ISolarInverterService> _activeInverterServices { get; set; } = new();

        public double TotalCapacity => _activeInverterServices.Sum(serv => serv.Endpoints.Sum(ep => ep.Value.InverterMaxCapacity));

        /// <summary>
        /// Exposes all active inverter services for consumers that need to iterate
        /// over individual inverters (e.g. to read ActualSolarPowerInWatts).
        /// </summary>
        public IReadOnlyList<ISolarInverterService> ActiveInverterServices
            => _activeInverterServices.AsReadOnly();

        /// <summary>
        /// True when ALL active inverters are reachable via their primary channel.
        /// </summary>
        public bool AllAvailable => _activeInverterServices.All(s => s.IsAvailable);

        /// <summary>
        /// True when AT LEAST ONE active inverter is reachable via its primary channel.
        /// </summary>
        public bool IsAvailable => _activeInverterServices.Any(s => s.IsAvailable);

        /// <summary>
        /// Returns the total current AC power in Watts across all inverters.
        /// Uses the primary (Modbus) value when available, falls back to cloud API
        /// per inverter when the primary channel is down and the inverter supports it.
        /// </summary>
        public async Task<double> GetActualSolarPowerInWatts()
        {
            double total = 0.0;

            foreach (var service in _activeInverterServices)
            {
                if (service.IsAvailable)
                {
                    total += service.ActualSolarPowerInWatts;
                }
                else if (service.SupportsFallback)
                {
                    total += await service.GetFallbackACPowerInWattsAsync().ConfigureAwait(false);
                }
                // else: inverter offline and no fallback — contribute 0W
            }

            return total;
        }

        // Health check interval in seconds.
        private const int HealthCheckIntervalSeconds = 30;

        public SolarInverterManager(IEnumerable<ISolarInverterService> inverterServices,
                                    LoggingService<SolarInverterManager> logger,
                                    TimeZoneService timezoneService,
                                    IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                    IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor)
        {
            _logger = logger;
            _timeZoneService = timezoneService;
            _settingsConfigMonitor = settingsConfigMonitor;
            _settingsConfig = settingsConfigMonitor.CurrentValue;
            _settingsConfigMonitor.OnChange((config) =>
            {
                _settingsConfig = config;
            });

            _powerSystemConfigMonitor = powerSystemsConfigMonitor;
            UpdatePowersystemConfig(inverterServices, _powerSystemConfigMonitor.CurrentValue);
            _powerSystemConfigMonitor.OnChange((config) =>
            {
                UpdatePowersystemConfig(inverterServices, config);
            });
        }

        private void UpdatePowersystemConfig(IEnumerable<ISolarInverterService> inverterServices, PowerSystemsConfig config)
        {
            _powerSystemsConfig = config;
            FillActiveInverterServices(inverterServices);
        }

        private void FillActiveInverterServices(IEnumerable<ISolarInverterService> inverterServices)
        {
            _activeInverterServices = inverterServices
                .Where(inverterService => _powerSystemsConfig.Endpoints.ContainsKey(inverterService.ProviderName))
                .ToList();
        }

        public async Task<double> GetTotalACPowerInWatts()
        {
            double total = 0;

            if (_timeZoneService.GetSunlightLevel(_settingsConfig.Latitude, _settingsConfig.Longitude) == SolCalc.Data.SunlightLevel.Daylight)
            {
                foreach (var service in _activeInverterServices)
                {
                    total += await service.GetTotalACPowerInWatts();
                }
            }

            return total;
        }

        public ISolarInverterService? GetByName(string name) =>
            _activeInverterServices.FirstOrDefault(s => s.ProviderName.Equals(name, StringComparison.OrdinalIgnoreCase));

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            // Start all inverter services.
            foreach (var service in _activeInverterServices)
            {
                await service.Start(cancellationToken);
            }

            // Run health check loop in parallel.
            await RunHealthCheckLoopAsync(cancellationToken);
        }

        /// <summary>
        /// Periodically checks whether the inverter is reachable by attempting
        /// to read the AC power. Updates IsAvailable accordingly.
        /// </summary>
        private async Task RunHealthCheckLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(HealthCheckIntervalSeconds), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                await CheckAvailabilityAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        // Inverter is considered offline when no successful read within this timeout.
        private const int AvailabilityTimeoutSeconds = 60;

        /// <summary>
        /// Checks availability of each inverter by inspecting the timestamp of the
        /// last successful Modbus read. This avoids making a new Modbus connection
        /// that would conflict with the Process() loop reading every second and
        /// cause "Connection reset by peer" flip-flop behavior.
        /// </summary>
        private Task CheckAvailabilityAsync()
        {
            foreach (var service in _activeInverterServices)
            {
                bool wasAvailable = service.IsAvailable;
                bool isNowAvailable = (_timeZoneService.Now - service.LastSuccessfulReadUtc).TotalSeconds
                                      < AvailabilityTimeoutSeconds;

                service.IsAvailable = isNowAvailable;

                if (isNowAvailable && !wasAvailable)
                    _logger.LogWarning($"Inverter '{service.ProviderName}' is back online.");
                else if (!isNowAvailable && wasAvailable)
                    _logger.LogWarning($"Inverter '{service.ProviderName}' is offline — no successful read in {AvailabilityTimeoutSeconds}s.");
            }

            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _activeInverterServices)
            {
                await service.Stop(cancellationToken);
            }
        }

        /// <summary>
        /// Last inverter setpoint sent via ThrottleInverterToWatts (W).
        /// Null = never set (interpret as full output = double.MaxValue).
        /// Used by HardwareStatusService to expose the current setpoint
        /// without an additional Modbus round-trip.
        /// </summary>
        public double? LastSetpointW { get; private set; } = null;

        private double? _lastWattsSet { get; set; } = null;

        public async Task ThrottleInverterToWatts(double watts)
        {
            if (TotalCapacity <= 0.0)
                throw new InvalidOperationException($"InverterMaxCapacity not set or wrong in config for one or more endpoints");

            if (!_lastWattsSet.HasValue || _lastWattsSet != watts)
            {
                foreach (var service in _activeInverterServices)
                {
                    if (!service.IsAvailable)
                    {
                        // Skip unavailable inverters — cannot throttle via Modbus.
                        _logger.LogWarning($"ThrottleInverterToWatts: skipping '{service.ProviderName}' — inverter offline.");
                        continue;
                    }

                    double serviceCapacity = service.TotalCapacity;
                    if (serviceCapacity <= 0.0)
                        continue;

                    var percentage = (ushort)Math.Min(100, watts / serviceCapacity * 100);
                    await service.ThrottleInverterToPercentage(percentage).ConfigureAwait(false);
                }
            }

            _lastWattsSet = watts;
            LastSetpointW = watts;
        }
    }
}