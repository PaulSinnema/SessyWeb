using SessyController.Configurations;
using static SessyController.Services.ActivePowerStrategy;

namespace SessyController.Services.Items
{
    public class Battery
    {
        private readonly SessyService _sessyService;
        private readonly LoggingService<Battery> _logger;

        private SessyBatteryEndpoint? _endpoint {  get; set; }
        private string Id { get; set; }
        private bool _initialized = false;

        public Battery(LoggingService<Battery> logger, SessyService sessyService)
        {
            _sessyService = sessyService;
            _logger = logger;
            Id = string.Empty;
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
            EnsureInitialized();
            return await _sessyService.GetPowerStatusAsync(Id);
        }

        public async Task<ActivePowerStrategy?> GetActivePowerStrategy()
        {
            EnsureInitialized();
            return await _sessyService.GetActivePowerStrategyAsync(Id);
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
                if(lastPowerSetpoint.Setpoint != powerSetpoint.Setpoint)
                    await _sessyService.SetPowerSetpointAsync(Id, powerSetpoint);
            }
            else
            {
                await _sessyService.SetPowerSetpointAsync(Id, powerSetpoint);
            }

            lastPowerSetpoint = powerSetpoint;
        }

        public async Task<double> GetFreeCapacity()
        {
            EnsureInitialized();

            PowerStatus? powerStatus = await GetPowerStatus();

            var remaining = _endpoint.Capacity * (1 - powerStatus.Sessy.StateOfCharge);

            return remaining;
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
    }
}
