using SessyCommon.Extensions;
using SessyController.Configurations;
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

        public async Task<PowerStatus?> GetPowerStatus()
        {
            var tries = 0;
            Exception? exception = null;

            EnsureInitialized();

            do
            {
                try
                {
                    return await _sessyService.GetPowerStatusAsync(Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    exception = ex;
                    tries++;
                    _logger.LogInformation($"Could not get power status for battery {Id}. Retry {tries}");

                    Thread.Sleep(5000); // wait a while.
                }
            } while (tries <= 10);

            throw new InvalidOperationException($"Could not get power status after 10 retries for battery {Id}", exception);
        }

        public async Task<ActivePowerStrategy?> GetActivePowerStrategy()
        {
            var tries = 0;
            Exception? exception = null;

            EnsureInitialized();

            do
            {
                try
                {
                    return await _sessyService.GetActivePowerStrategyAsync(Id);
                }
                catch (Exception ex)
                {
                    exception = ex;
                    tries++;
                    _logger.LogInformation($"Could not get active power strategy for battery {Id}. Retry {tries}");

                    Thread.Sleep(5000); // wait a while.
                }

            }
            while (tries < 10);

            throw new InvalidOperationException($"Could not get active power strategy after 10 retries for battery {Id}", exception);
        }

        public async Task SetActivePowerStrategy(ActivePowerStrategy strategy)
        {
            EnsureInitialized();

            var currentPowerStrategy = await GetActivePowerStrategy();

            if (currentPowerStrategy != null)
            {
                if (currentPowerStrategy.Strategy != strategy.Strategy)
                {
                    _logger.LogInformation($"Changing strategy to {strategy.Strategy}: 1");

                    await _sessyService.SetActivePowerStrategyAsync(Id, strategy);
                }
            }
            else
            {
                _logger.LogInformation($"Changing strategy to {strategy.Strategy}: 2");

                await _sessyService.SetActivePowerStrategyAsync(Id, strategy);
            }
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

        private PowerSetpoint? lastPowerSetpoint = null;

        public async Task SetPowerSetpointAsync(PowerSetpoint powerSetpoint)
        {
            EnsureInitialized();

            if (lastPowerSetpoint != null)
            {
                if (lastPowerSetpoint.Setpoint != powerSetpoint.Setpoint)
                {
                    await _sessyService.SetPowerSetpointAsync(Id, powerSetpoint);
                }
            }
            else
            {
                await _sessyService.SetPowerSetpointAsync(Id, powerSetpoint);
            }

            lastPowerSetpoint = powerSetpoint;
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
        /// Get power strategy and EPEX prices from the battery
        /// </summary>
        public async Task<DynamicStrategy?> GetDynamicScheduleAsync()
        {
            return await _sessyService.GetDynamicScheduleAsync(Id);
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
