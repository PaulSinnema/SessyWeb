using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.InverterServices;

namespace SessyController.Services.Items
{
    /// <summary>
    /// This class is a container for all betteries.
    /// </summary>
    public class BatteryContainer
    {
        private SessyBatteryConfig _sessyBatteryConfig;
        private SolarEdgeInverterService _solarEdgeService;

        public List<Battery>? Batteries { get; set; }

        public BatteryContainer(IServiceScopeFactory serviceScopeFactory,
                                IOptions<SessyBatteryConfig> sessyBatteryConfig,
                                SolarEdgeInverterService solarEdgeService)
        {
            _sessyBatteryConfig = sessyBatteryConfig.Value;
            _solarEdgeService = solarEdgeService;

            using var scope = serviceScopeFactory.CreateScope();

            Batteries = GetBatteries(scope.ServiceProvider);
        }

        /// <summary>
        /// Load information from appsettings for the batteries.
        /// </summary>
        private List<Battery> GetBatteries(IServiceProvider serviceProvider)
        {
            var batteries = new List<Battery>();

            foreach (var batteryConfig in _sessyBatteryConfig.Batteries)
            {
                using (var scope = serviceProvider.CreateScope())
                {
                    var battery = scope.ServiceProvider.GetRequiredService<Battery>();

                    battery.Inject(batteryConfig.Key, batteryConfig.Value);

                    batteries.Add(battery);
                }
            }

            return batteries;
        }

        /// <summary>
        /// State of charge of all Sessy batteries.
        /// 1 = 100%.
        /// </summary>
        public async Task<double> GetStateOfCharge()
        {
            double stateOfCharge = 0.0;
            double count = 0;

            foreach(var battery in Batteries)
            {
                PowerStatus? powerStatus = await battery.GetPowerStatus();

                stateOfCharge += powerStatus.Sessy.StateOfCharge;
                count++;
            }

            if(count > 0)
                return stateOfCharge / count;

            return 0.0;
        }

        /// <summary>
        /// State of charge of all Sessy batteries in Watts.
        /// </summary>
        /// <returns></returns>
        public async Task<double> GetStateOfChargeInWatts()
        {
            return (await GetStateOfCharge()) * GetTotalCapacity();
        }

        /// <summary>
        /// Get the total capacity of all batteries.
        /// </summary>
        public double GetTotalCapacity()
        {
            return Batteries!.Sum(bat => bat.GetCapacity());
        }

        /// <summary>
        /// Get the total charging capacity for all batteries.
        /// </summary>
        public double GetChargingCapacity()
        {
            return Batteries!.Sum(bat => bat.GetMaxCharge());
        }

        /// <summary>
        /// Get the total discharging capacity for all batteries.
        /// </summary>
        public double GetDischargingCapacity()
        {
            return Batteries!.Sum(bat => bat.GetMaxDischarge());
        }

        /// <summary>
        /// Start Charged Net Zero Home algorithm.
        /// </summary>
        public async Task StartNetZeroHome()
        {
            foreach (var bat in Batteries)
            {
                await bat.SetActivePowerStrategyToZeroNetHome();
            }
        }

        /// <summary>
        /// Start charging cycle.
        /// </summary>
        public async Task StartCharging(double chargingPower)
        {
            foreach (var bat in Batteries)
            {
                var maxChargingPower = bat.GetMaxCharge();
                var totalCapacity = GetChargingCapacity();
                var powerToUse = (maxChargingPower / totalCapacity) * chargingPower;

                await bat.SetActivePowerStrategyToOpenAPI();
                await bat.SetPowerSetpointAsync(GetSetpoint(bat, -(Convert.ToInt16(powerToUse))));
            }
        }

        /// <summary>
        /// Start discharging cycle.
        /// </summary>
        public async Task StartDisharging(double dischargingPower)
        {
            foreach (var bat in Batteries)
            {
                var maxDischargingPower = bat.GetMaxDischarge();
                var totalCapacity = GetDischargingCapacity();
                var powerToUse = (maxDischargingPower / totalCapacity) * dischargingPower;

                await bat.SetActivePowerStrategyToOpenAPI();
                await bat.SetPowerSetpointAsync(GetSetpoint(bat, Convert.ToInt16(powerToUse)));
            }
        }

        /// <summary>
        /// Stop charging.
        /// </summary>
        public async Task StopAll()
        {
            foreach (var bat in Batteries)
            {
                await bat.SetActivePowerStrategyToOpenAPI();
                await bat.SetPowerSetpointAsync(GetSetpoint(bat, 0));
            }
        }

        /// <summary>
        /// Get the setpoint for (dis)charging.
        /// Positive is discharging
        /// Negative is charging
        /// </summary>
        private static PowerSetpoint GetSetpoint(Battery bat, int setpoint)
        {
            return new PowerSetpoint { Setpoint = setpoint };
        }

        public async Task<double> GetBatterPercentage()
        {
            var watts = await GetStateOfChargeInWatts();
            var totalCapacity = GetTotalCapacity();

            return watts / totalCapacity;
        }

        public async Task<double> GetTotalPowerInWatts()
        {
            var totalPower = 0.0;

            foreach (var battery in Batteries)
            {
                totalPower += await battery.GetPowerInWatts();
            }

            return totalPower;
        }
    }
}
