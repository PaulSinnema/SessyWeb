using SessyCommon.Configurations;
using static SessyController.Services.ActivePowerStrategy;

namespace SessyController.Services.Items
{
    public class Battery : IDisposable
    {
        private readonly SessyService _sessyService;
        private readonly LoggingService<Battery> _logger;

        private SessyBatteryEndpoint? _endpoint { get; set; }
        public string Id { get; private set; }
        private bool _initialized = false;
        public double SetpointRequested { get; set; }

        public Battery(LoggingService<Battery> logger, SessyService sessyService)
        {
            _sessyService = sessyService;
            _logger = logger;
            Id = string.Empty;
        }

        public string? GetName()
        {
            return _endpoint.Name;
        }

        public double GetCapacity()
        {
            return _endpoint.Capacity;
        }

        public double GetMaxCharge()
        {
            return _endpoint.MaxCharge;
        }

        public double GetMaxDischarge()
        {
            return _endpoint.MaxDischarge;
        }

        public async Task<double> GetPowerInWatts()
        {
            var powerStatus = await GetPowerStatus();

            return powerStatus?.Sessy?.Power ?? 0.0;
        }

        /// <summary>
        /// Current power status, cached for <see cref="StatusTtl"/>.
        ///
        /// Every browser circuit used to poll this itself — the charging-hours page every 5 seconds
        /// for all three batteries, plus BatteryInfo once more per battery — so the load on the
        /// hardware scaled with the number of open tabs. The cache makes it scale with time
        /// instead, and the lock collapses simultaneous callers into a single request. The TTL is
        /// far below the control loop's own interval, so nothing decides on stale data.
        /// </summary>
        public async Task<PowerStatus?> GetPowerStatus()
        {
            EnsureInitialized();

            if (TryGetFresh(_powerStatus, _powerStatusAt, out var cached))
                return cached;

            await _powerStatusLock.WaitAsync().ConfigureAwait(false);

            try
            {
                // Another caller may have refreshed it while we waited for the lock.
                if (TryGetFresh(_powerStatus, _powerStatusAt, out cached))
                    return cached;

                var result = await FetchWithRetries(
                    () => _sessyService.GetPowerStatusAsync(Id), "power status").ConfigureAwait(false);

                _powerStatus = result;
                _powerStatusAt = DateTime.UtcNow;

                return result;
            }
            finally
            {
                _powerStatusLock.Release();
            }
        }

        /// <summary>How long a hardware reading is served from cache.</summary>
        private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Retries left deliberately short. It used to be 10 attempts with a 5 second wait between
        /// them, on an HttpClient without a timeout — an unreachable battery could occupy the
        /// caller for a quarter of an hour.
        /// </summary>
        private const int MaxTries = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        private PowerStatus? _powerStatus;
        private DateTime _powerStatusAt = DateTime.MinValue;
        private readonly SemaphoreSlim _powerStatusLock = new(1, 1);

        private ActivePowerStrategy? _activeStrategy;
        private DateTime _activeStrategyAt = DateTime.MinValue;
        private readonly SemaphoreSlim _activeStrategyLock = new(1, 1);

        private static bool TryGetFresh<T>(T? value, DateTime readAt, out T? fresh) where T : class
        {
            fresh = value;

            return value != null && DateTime.UtcNow - readAt < StatusTtl;
        }

        /// <summary>Invalidates the cached readings after a command that changes them.</summary>
        private void InvalidateStatusCache()
        {
            _powerStatusAt = DateTime.MinValue;
            _activeStrategyAt = DateTime.MinValue;
        }

        private async Task<T?> FetchWithRetries<T>(Func<Task<T?>> fetch, string what)
        {
            Exception? exception = null;

            for (var attempt = 1; attempt <= MaxTries; attempt++)
            {
                try
                {
                    return await fetch().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    exception = ex;
                    _logger.LogInformation($"Could not get {what} for battery {Id}. Retry {attempt}");

                    if (attempt < MaxTries)
                        await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException($"Could not get {what} after {MaxTries} tries for battery {Id}", exception);
        }

        /// <summary>Active strategy, cached the same way as GetPowerStatus.</summary>
        public async Task<ActivePowerStrategy?> GetActivePowerStrategy()
        {
            EnsureInitialized();

            if (TryGetFresh(_activeStrategy, _activeStrategyAt, out var cached))
                return cached;

            await _activeStrategyLock.WaitAsync().ConfigureAwait(false);

            try
            {
                if (TryGetFresh(_activeStrategy, _activeStrategyAt, out cached))
                    return cached;

                var result = await FetchWithRetries(
                    () => _sessyService.GetActivePowerStrategyAsync(Id), "active power strategy").ConfigureAwait(false);

                _activeStrategy = result;
                _activeStrategyAt = DateTime.UtcNow;

                return result;
            }
            finally
            {
                _activeStrategyLock.Release();
            }
        }

        private async Task SetActivePowerStrategy(ActivePowerStrategy strategy, bool handover = false)
        {
            EnsureInitialized();

            var currentPowerStrategy = await GetActivePowerStrategy();

            if (currentPowerStrategy != null)
            {
                if (currentPowerStrategy.Strategy != strategy.Strategy)
                {
                    _logger.LogInformation($"Changing strategy to {strategy.Strategy}: 1");

                    await _sessyService.SetActivePowerStrategyAsync(Id, strategy, handover);
                    InvalidateStatusCache();
                }
            }
            else
            {
                _logger.LogInformation($"Changing strategy to {strategy.Strategy}: 2");

                await _sessyService.SetActivePowerStrategyAsync(Id, strategy, handover);
                InvalidateStatusCache();
            }
        }

        /// <summary>
        /// Hand the battery to Sessy's own ROI (Dynamic) strategy. Only used when control is handed
        /// to Charged, so it passes handover: the normal write guard blocks exactly this case.
        /// </summary>
        public async Task SetActivePowerStrategyToRoi()
        {
            _logger.LogInformation("Setting strategy to ROI (Dynamic)");

            await SetActivePowerStrategy(
                new ActivePowerStrategy { Strategy = PowerStrategies.POWER_STRATEGY_ROI.ToString() },
                handover: true);
        }

        /// <summary>Hand the battery to Sessy's own ECO strategy. See SetActivePowerStrategyToRoi.</summary>
        public async Task SetActivePowerStrategyToEco()
        {
            _logger.LogInformation("Setting strategy to ECO");

            await SetActivePowerStrategy(
                new ActivePowerStrategy { Strategy = PowerStrategies.POWER_STRATEGY_ECO.ToString() },
                handover: true);
        }

        public async Task SetActivePowerStrategyToOpenAPI()
        {
            _logger.LogInformation("Setting strategy to Open API");

            await SetActivePowerStrategy(new ActivePowerStrategy { Strategy = PowerStrategies.POWER_STRATEGY_API.ToString() });
        }

        public async Task SetActivePowerStrategyToZeroNetHome()
        {
            _logger.LogInformation("Setting strategy to Net Zero Home");

            await SetActivePowerStrategy(new ActivePowerStrategy { Strategy = PowerStrategies.POWER_STRATEGY_NOM.ToString() });
        }

        public async Task SetPowerSetpointAsync(PowerSetpoint powerSetpoint)
        {
            EnsureInitialized();

            SetpointRequested = powerSetpoint.Setpoint;

            var powerStatus = await GetPowerStatus().ConfigureAwait(false);

            if (powerStatus.Sessy.PowerSetpoint != powerSetpoint.Setpoint)
            {
                await _sessyService.SetPowerSetpointAsync(Id, powerSetpoint).ConfigureAwait(false);
                InvalidateStatusCache();
            }
        }

        /// <summary>
        /// Get the charged capacity for a battery
        /// </summary>
        public async Task<double> GetChargedCapacity()
        {
            EnsureInitialized();

            PowerStatus? powerStatus = await GetPowerStatus().ConfigureAwait(false);

            var charged = _endpoint.Capacity * powerStatus.Sessy.StateOfCharge;

            return charged;
        }

        /// <summary>
        /// Get the remaining capacity for a battery
        /// </summary>
        public async Task<double> GetFreeCapacity()
        {
            EnsureInitialized();

            PowerStatus? powerStatus = await GetPowerStatus().ConfigureAwait(false);

            var remaining = _endpoint.Capacity * (1 - powerStatus.Sessy.StateOfCharge);

            return remaining;
        }

        /// <summary>
        /// Get the schedule the battery planned for itself, plus the EPEX prices it used.
        /// </summary>
        public async Task<SessyScheduleResponse?> GetScheduleAsync()
        {
            return await _sessyService.GetScheduleAsync(Id);
        }

        internal void Inject(string id, SessyBatteryEndpoint endpoint)
        {
            Id = id;
            _endpoint = endpoint;
            _initialized = true;
        }

        private void EnsureInitialized()
        {
            if (!_initialized) throw new InvalidOperationException("Battery object not initialized");
        }

        public void Dispose()
        {

        }
    }
}