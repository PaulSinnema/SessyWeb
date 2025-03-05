using Microsoft.Extensions.Options;
using SessyController.Configurations;
using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class HourlyInfo : IDisposable
    {
        public HourlyInfo(DateTime time, double price, IServiceScopeFactory serviceScopeFactory)
        {
            Time = time;
            Price = price;
            _serviceScopeFactory = serviceScopeFactory;

            _scope = _serviceScopeFactory.CreateScope();

            _settingsConfigMonitor = _scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SettingsConfig>>();

            _settingsConfigSubscription = _settingsConfigMonitor.OnChange((SettingsConfig settings) => _settingsConfig = settings);

            _settingsConfig = _settingsConfigMonitor.CurrentValue;
        }

        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private IDisposable? _settingsConfigSubscription { get; set; }
        private SettingsConfig _settingsConfig;

        /// <summary>
        /// Price from ENTSO-E
        /// </summary>
        public double Price { get; set; }

        public double SmoothedPrice { get; private set; }

        /// <summary>
        /// Timestamp from ENTSO-E
        /// </summary>
        public DateTime Time { get; set; }

        private IServiceScopeFactory _serviceScopeFactory;
        private IServiceScope _scope { get; set; }

        /// <summary>
        /// How much profit does this (dis)charge give?
        /// </summary>
        public double Profit => Selling - Buying;

        public double ProfitVisual => Profit / 10;

        public double Buying { get; set; }

        public double Selling { get; set; }

        public double ChargeLeft { get; set; }

        public double ChargeLeftPercentage { get; set; }

        public double ChargeLeftVisual => ChargeLeft / 100000;

        public double SolarGlobalRadiation { get; set; }

        public double SolarPower { get; set; }

        public double SolarPowerInWatts => SolarPower * 1000;

        public double SolarPowerVisual => SolarPower / 10 / _settingsConfig.SolarCorrection;

        private bool _charging = false;

        public void Reset()
        {
            Charging = false;
            Discharging = false;
            SolarPower = 0.0;
            ChargeLeft = 0.0;
            Buying = 0.0;
            Selling = 0.0;
            ChargeLeft = 0.0;
            ChargeLeftPercentage = 0.0;
            SolarGlobalRadiation = 0.0;
            SolarPower = 0.0;
            SmoothedPrice = 0.0;
        }

        /// <summary>
        /// If true charging is requested.
        /// </summary>
        public bool Charging
        {
            get
            {

                return _charging;
            }
            private set
            {
                if (value)
                {
                    _discharging = false;
                }

                _charging = value;
            }
        }


        private bool _discharging = false;

        /// <summary>
        /// If true charging is requested.
        /// </summary>
        public bool Discharging
        {
            get
            {
                return _discharging;
            }
            private set
            {

                if (value)
                {
                    _charging = false;
                }

                _discharging = value;
            }
        }

        /// <summary>
        /// Adds a gesmoothed price to each HourlyInfo object in the list.
        /// </summary>
        public static void AddSmoothedPrices(List<HourlyInfo> hourlyInfos, int windowSize = 3)
        {
            if (hourlyInfos == null || hourlyInfos.Count == 0) return;

            for (int i = 0; i < hourlyInfos.Count; i++)
            {
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(hourlyInfos.Count - 1, i + windowSize / 2);

                var range = hourlyInfos.Skip(start).Take(end - start + 1);

                double average = range.Count() > 0 ? range.Average(h => h.Price) : 0.0;

                hourlyInfos[i].SmoothedPrice = average;
            }
        }

        public void DisableCharging()
        {
            Charging = false;
        }

        public void DisableDischarging()
        {
            Discharging = false;
        }

        public void SetModes(Modes mode)
        {
            switch (mode)
            {
                case Modes.Charging:
                    Charging = true;
                    break;

                case Modes.Discharging:
                    Discharging = true;
                    break;

                default:
                    throw new InvalidOperationException("Mode wrong");
            }
        }

        /// <summary>
        /// Helper for visualization
        /// </summary>
        public double VisualizeInChart
        {
            get
            {
                if (Charging)
                    return -0.2;
                else if (Discharging)
                    return 0.2;
                else if (ZeroNetHome)
                    return 0.03;

                return 0.0;
            }
        }

        public string DisplayState
        {
            get
            {
                return Charging ? "Charging" :
                          Discharging ? "Discharging" :
                          ZeroNetHome ? "Net zero home" :
                          Disabled ? "Disabled" : "Wrong state";
            }
        }

        /// <summary>
        /// If no (dis)charging is in progress Net Zero Home is requested.
        /// </summary>
        public bool ZeroNetHome
        {
            get => !(Charging || Discharging) &&
                (Profit >= _settingsConfig.NetZeroHomeMinProfit ||
                SolarPowerInWatts > 100.0);
        }

        /// <summary>
        /// If no (dis)charging or Zero net home is in progress Sessy's are requested to disable.
        /// </summary>
        public bool Disabled => !(Charging || Discharging || ZeroNetHome);

        /// <summary>
        /// For better debugging
        /// </summary>
        public override string ToString()
        {
            return $"{Time}: Charging: {Charging}, Discharging: {Discharging}, Zero Net Home: {ZeroNetHome}, Price: {Price}, Charge left: {ChargeLeft}, Solar power {SolarPower}";
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _settingsConfigSubscription.Dispose();
                _scope.Dispose();

                _isDisposed = true;
            }
        }
    }
}
