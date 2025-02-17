using Microsoft.Extensions.Options;
using SessyController.Configurations;

namespace SessyController.Services.Items
{
    /// <summary>
    /// This class is a container for all betteries.
    /// </summary>
    public class BatteryContainer
    {
        private SessyBatteryConfig _sessyBatteryConfig;

        public List<Battery>? Batteries { get; set; }

        public BatteryContainer(IServiceScopeFactory serviceScopeFactory,
                                IOptions<SessyBatteryConfig> sessyBatteryConfig)
        {
            _sessyBatteryConfig = sessyBatteryConfig.Value;

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
        /// Get the total capacity of all batteries.
        /// </summary>
        public double GetTotalCapacity()
        {
            return Batteries.Sum(bat => bat.GetCapacity());
        }

        /// <summary>
        /// Get the total charging capacity for all batteries.
        /// </summary>
        public double GetChargingCapacity()
        {
            return Batteries.Sum(bat => bat.GetMaxCharge());
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
        public void StartNetZeroHome()
        {
            Batteries.ForEach(async bat =>
            {
                await bat.SetActivePowerStrategyToZeroNetHome();
            });
        }

        /// <summary>
        /// Start charging cycle.
        /// </summary>
        public void StartCharging()
        {
            Batteries.ForEach(async bat =>
            {
                await bat.SetActivePowerStrategyToOpenAPI();
                await bat.SetPowerSetpointAsync(GetSetpoint(bat, -(Convert.ToInt16(bat.GetMaxCharge()))));
            });
        }

        /// <summary>
        /// Start discharging cycle.
        /// </summary>
        public void StartDisharging()
        {
            Batteries.ForEach(async bat =>
            {
                await bat.SetActivePowerStrategyToOpenAPI();
                await bat.SetPowerSetpointAsync(GetSetpoint(bat, Convert.ToInt16(bat.GetMaxDischarge())));
            });
        }

        /// <summary>
        /// Stop charging.
        /// </summary>
        public void StopAll()
        {
            Batteries.ForEach(async bat =>
            {
                await bat.SetActivePowerStrategyToOpenAPI();
                await bat.SetPowerSetpointAsync(GetSetpoint(bat, 0));
            });
        }

        /// <summary>
        /// Set the setpoint for (dis)charging.
        /// Positive is discharging
        /// Negative is charging
        /// </summary>
        private static PowerSetpoint GetSetpoint(Battery bat, int setpoint)
        {
            return new PowerSetpoint { Setpoint = setpoint };
        }
    }
}
