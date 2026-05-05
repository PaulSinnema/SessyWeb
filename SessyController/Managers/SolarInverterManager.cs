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
            // Start all inverter services and the health check loop in parallel.
            // service.Start() contains an infinite loop and never returns,
            // so we must not await it sequentially before starting the health check.
            var tasks = _activeInverterServices
                .Select(service => service.Start(cancellationToken))
                .ToList();

            tasks.Add(RunHealthCheckLoopAsync(cancellationToken));

            await Task.WhenAll(tasks).ConfigureAwait(false);
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
                    await CheckAvailabilityAsync().ConfigureAwait(false);

                    await Task.Delay(TimeSpan.FromSeconds(HealthCheckIntervalSeconds), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// <summary>
        /// Checks availability of each inverter individually by attempting a lightweight
        /// Modbus read. Sets IsAvailable on each service based on whether the read succeeds.
        /// Logs only on transitions between online and offline.
        /// </summary>
        private async Task CheckAvailabilityAsync()
        {
            foreach (var service in _activeInverterServices)
            {
                bool wasAvailable = service.IsAvailable;

                try
                {
                    // Use internal method that bypasses the IsAvailable check,
                    // so we can detect when an offline inverter comes back online.
                    await service.CheckAvailabilityAsync().ConfigureAwait(false);

                    service.IsAvailable = true;

                    if (!wasAvailable)
                        _logger.LogWarning($"Inverter '{service.ProviderName}' is back online.");
                }
                catch (Exception ex)
                {
                    service.IsAvailable = false;

                    if (wasAvailable)
                        _logger.LogWarning($"Inverter '{service.ProviderName}' is offline — Modbus TCP unreachable: {ex.Message}");
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _activeInverterServices)
            {
                await service.Stop(cancellationToken);
            }
        }

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
        }
    }
}