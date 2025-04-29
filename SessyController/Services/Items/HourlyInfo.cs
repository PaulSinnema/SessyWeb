using SessyCommon.Extensions;
using SessyController.Configurations;
using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class HourlyInfo
    {
        public HourlyInfo(DateTime time,
                          double? buyPrice,
                          double? sellPrice,
                          SettingsConfig settingsConfig,
                          BatteryContainer batteryContainer,
                          SolarEdgeService solarEdgeService,
                          TimeZoneService timeZoneService)
        {
            Time = time;
            BuyingPrice = buyPrice.HasValue ? buyPrice.Value : 0.0;
            SellingPrice = sellPrice.HasValue ? sellPrice.Value : 0.0;

            _settingsConfig = settingsConfig;
            _batteryContainer = batteryContainer;
            _solarEdgeService = solarEdgeService;
            _timeZoneService = timeZoneService;

            TotalCapacity = _batteryContainer.GetTotalCapacity();

            if (!(TotalCapacity > 0.0))
                throw new InvalidOperationException("The total capacity should not be 0.0");
        }

        private SettingsConfig _settingsConfig { get; set; }

        private BatteryContainer _batteryContainer { get; set; }

        private SolarEdgeService _solarEdgeService { get; set; }

        private TimeZoneService _timeZoneService { get; set; }

        /// <summary>
        /// Price from ENTSO-E
        /// </summary>
        public double BuyingPrice { get; set; }

        public double SellingPrice { get; set; }

        public double SmoothedPrice { get; private set; }
        public double SmoothedSolarPower { get; set; }

        /// <summary>
        /// Timestamp from ENTSO-E
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// How much profit does this (dis)charge give?
        /// </summary>
        public double Profit => Selling - Buying;

        /// <summary>
        /// This is the profit if Net Zero Home were enabled. It is used
        /// to determine if NetZeroHome should be enabled or not.
        /// </summary>
        public double NetZeroHomeProfit { get; set; }

        public double ProfitVisual => Profit / 10;

        public double Buying { get; set; }

        public double Selling { get; set; }

        private double _chargeLeft;
        private bool _chargeLeftSet = false;

        public double ChargeLeft 
        {
            get
            {
                if (!_chargeLeftSet)
                    throw new InvalidOperationException($"Cannot use charge left before it is set.{this}");

                return _chargeLeft;
            }
            set
            {
                _chargeLeft = value;
                _chargeLeftSet = true;
            }
        }

        private double _chargeNeeded;
        private bool _chargeNeededSet = false;

        public double ChargeNeeded
        {
            get
            {
                //if (!_chargeNeededSet)
                //    throw new InvalidOperationException($"Cannot use charge needed before it is set. {this}");

                return _chargeNeeded;
            }
            set
            {
                _chargeNeeded = value;
                _chargeNeededSet = true;
            }
        }

        public double ChargeNeededVisual => _chargeNeeded / 100000;

        private double TotalCapacity { get; set; }

        public double ChargeLeftPercentage => _chargeLeft / (TotalCapacity / 100.0);

        public double ChargeLeftVisual => _chargeLeft / 100000;

        public double SolarGlobalRadiation { get; set; }

        public double SolarPower { get; set; }

        public double SolarPowerInWatts => SolarPower * 1000;

        public double SolarPowerVisual => SmoothedSolarPower / 2.5 / _settingsConfig.SolarCorrection;

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
            SolarGlobalRadiation = 0.0;
            SmoothedPrice = 0.0;
        }

        public Modes Mode
        {
            get
            {
                if (Charging) return Modes.Charging;
                if (Discharging) return Modes.Discharging;
                if (NetZeroHomeWithSolar) return Modes.ZeroNetHome;
                if (Disabled) return Modes.Disabled;
                return Modes.Unknown;
            }
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
        public static void AddSmoothedPrices(List<HourlyInfo> hourlyInfos, int windowSize = 4)
        {
            if (hourlyInfos == null || hourlyInfos.Count == 0) return;

            for (int i = 0; i < hourlyInfos.Count; i++)
            {
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(hourlyInfos.Count - 1, i + windowSize / 2);

                var range = hourlyInfos.Skip(start).Take(end - start + 1);

                double average = range.Count() > 0 ? range.Average(h => h.BuyingPrice) : 0.0;

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
                else if (NetZeroHomeWithSolar)
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
                          NetZeroHomeWithSolar ? "Net zero home" :
                          Disabled ? "Disabled" : "Wrong state";
            }
        }

        private bool SolarPowerIsActive
        {
            get
            {
                var now = _timeZoneService.Now;

                if (Time == now.DateFloorQuarter() && _solarEdgeService.ActualSolarPowerInWatts > 100.0)
                    return true;

                return false;
            }
        }

        public bool NetZeroHomeWithoutSolar
        {
            get => NetZeroHomeWithSolar && SolarPowerInWatts <= 100.0;
        }

        /// <summary>
        /// If no (dis)charging is in progress Net Zero Home is requested.
        /// </summary>
        public bool NetZeroHomeWithSolar
        {
            get => (!(Charging || Discharging)) &&
                (
                    NetZeroHomeProfit >= _settingsConfig.NetZeroHomeMinProfit ||
                    SolarPowerInWatts > 100.0 ||
                    SolarPowerIsActive
                );
        }

        /// <summary>
        /// If no (dis)charging or Zero net home is in progress Sessy's are requested to disable.
        /// </summary>
        public bool Disabled => !(Charging || Discharging || NetZeroHomeWithSolar);

        /// <summary>
        /// For better debugging
        /// </summary>
        public override string ToString()
        {
            return $"{Time}: Charging: {Charging}, Discharging: {Discharging}, Zero Net Home: {NetZeroHomeWithSolar}, Price: {BuyingPrice}, Charge left: {_chargeLeft}, Charge needed: {_chargeNeeded}, Solar power {SolarPower}";
        }
    }
}
