using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.InverterServices;
using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class QuarterlyInfo
    {
        private const double minSolarPower = 0.0;

        private QuarterlyInfo(DateTime time,
                              double marketPrice,
                                double buyingPrice,
                                double sellingPrice,
                                SettingsConfig settingsConfig,
                                BatteryContainer batteryContainer,
                                SolarEdgeInverterService solarEdgeService,
                                TimeZoneService timeZoneService,
                                CalculationService calculationService)
        {
            Time = time;

            _settingsConfig = settingsConfig;
            _batteryContainer = batteryContainer;
            _solarEdgeService = solarEdgeService;
            _timeZoneService = timeZoneService;
            _calculationService = calculationService;

            MarketPrice = marketPrice;
            BuyingPrice = buyingPrice;
            SellingPrice = sellingPrice;

            TotalCapacityInWatts = _batteryContainer.GetTotalCapacity();
            ChargingCapacityInWatts = _batteryContainer.GetChargingCapacityInWatts();
            DischargingCapacityInWatts = _batteryContainer.GetDischargingCapacityInWatts();

            if (TotalCapacityInWatts <= 0.0)
                throw new InvalidOperationException("The total capacity should not be 0.0");
        }

        // Factory that does the awaiting
        public static async Task<QuarterlyInfo> CreateAsync(
            DateTime time,
            double marketPrice,
            SettingsConfig settingsConfig,
            BatteryContainer batteryContainer,
            SolarEdgeInverterService solarEdgeService,
            TimeZoneService timeZoneService,
            CalculationService calculationService)
        {
            double buying = await calculationService.CalculateEnergyPrice(time, true) ?? 0.0;
            double selling = await calculationService.CalculateEnergyPrice(time, false) ?? 0.0;

            return new QuarterlyInfo(
                time,
                marketPrice,
                buying,
                selling,
                settingsConfig,
                batteryContainer,
                solarEdgeService,
                timeZoneService,
                calculationService);
        }

        private SettingsConfig _settingsConfig { get; set; }

        private BatteryContainer _batteryContainer { get; set; }

        private SolarEdgeInverterService _solarEdgeService { get; set; }

        private TimeZoneService _timeZoneService { get; set; }

        private CalculationService _calculationService { get; set; }

        /// <summary>
        /// Price from ENTSO-E
        /// </summary>
        public double MarketPrice { get; private set; }

        public double BuyingPrice { get; private set; }
        public double SellingPrice { get; private set; }
        public double Price => Charging ? BuyingPrice : SellingPrice;

        public double SmoothedBuyingPrice { get; private set; }
        public double SmoothedSellingPrice { get; private set; }
        public double SmoothedPrice => Charging ? SmoothedBuyingPrice : SmoothedSellingPrice;


        public double SmoothedSolarPower { get; set; }

        /// <summary>
        /// Timestamp from ENTSO-E
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// How much profit does this (dis)charge give?
        /// </summary>
        public double Profit
        {
            get
            {
                switch (Mode)
                {
                    case Modes.Charging:
                        return -ChargingCost;

                    case Modes.Discharging:
                        return DischargingCost;

                    case Modes.ZeroNetHome:
                    case Modes.Disabled:
                    default:
                        return 0.0;
                }
            }
        }

        public double EstimatedConsumptionPerQuarterHour { get; set; }

        /// <summary>
        /// This is the profit if Net Zero Home were enabled. It is used
        /// to determine if NetZeroHome should be enabled or not.
        /// </summary>
        public double NetZeroHomeProfit { get; set; }

        public double Buying { get; set; }

        public double Selling { get; set; }

        private double _chargeLeft;
        // private bool _chargeLeftSet = false;

        public double ChargeLeft
        {
            get
            {
                // TODO: There seem to be situations where this exception is thrown.
                //if (!_chargeLeftSet)
                //    throw new InvalidOperationException($"Cannot use charge left before it is set.{this}");

                return _chargeLeft;
            }
            private set
            {
                _chargeLeft = value;
                // _chargeLeftSet = true;
            }
        }

        public void SetChargeLeft(double chargeLeft)
        {
            ChargeLeft = chargeLeft;
        }

        private double _chargeNeeded;
        // private bool _chargeNeededSet = false;

        public double ChargeNeeded
        {
            get
            {
                // TODO: There seem to be situations where this exception is thrown.
                //if (!_chargeNeededSet)
                //    throw new InvalidOperationException($"Cannot use charge needed before it is set. {this}");

                return _chargeNeeded;
            }
            private set
            {
                _chargeNeeded = value;
                // _chargeNeededSet = true;
            }
        }

        public void SetChargeNeeded(double chargeNeeded)
        {
            ChargeNeeded = chargeNeeded;
        }

        public double ToChargeInWatts => Math.Max(0.0, Math.Min(ChargingCapacityInWatts, _chargeNeeded - _chargeLeft - SolarPowerPerQuarterInWatts));

        public double ToDischargeInWatts => Math.Max(0.0, Math.Min(DischargingCapacityInWatts, _chargeLeft - _chargeNeeded - SolarPowerPerQuarterInWatts));

        public double ChargingCost => ToChargeInWatts / 1000 / 4.0 * BuyingPrice;

        public double DischargingCost => ToDischargeInWatts / 1000 / 4.0 * SellingPrice;

        private double TotalCapacityInWatts { get; }
        public double ChargingCapacityInWatts { get; }
        public double DischargingCapacityInWatts { get; }

        public double ChargeLeftPercentage => _chargeLeft / (TotalCapacityInWatts / 100.0);

        public double SolarGlobalRadiation { get; set; }

        public double SolarPowerPerQuarterHour { get; set; }

        public double SolarPowerPerQuarterInWatts => SolarPowerPerQuarterHour * 1000;

        private bool _charging = false;

        public void Reset()
        {
            Charging = false;
            Discharging = false;
            SolarPowerPerQuarterHour = 0.0;
            ChargeLeft = 0.0;
            Buying = 0.0;
            Selling = 0.0;
            ChargeLeft = 0.0;
            SolarGlobalRadiation = 0.0;
            SmoothedBuyingPrice = 0.0;
            SmoothedSellingPrice = 0.0;
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
        public static void AddSmoothedPrices(List<QuarterlyInfo> hourlyInfos, int windowSize = 4)
        {
            if (hourlyInfos == null || hourlyInfos.Count == 0) return;

            for (int i = 0; i < hourlyInfos.Count; i++)
            {
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(hourlyInfos.Count - 1, i + windowSize / 2);

                var range = hourlyInfos.Skip(start).Take(end - start + 1);

                double buyingAverage = range.Count() > 0 ? range.Average(h => h.BuyingPrice) : 0.0;
                double sellingAverage = range.Count() > 0 ? range.Average(h => h.SellingPrice) : 0.0;

                hourlyInfos[i].SmoothedBuyingPrice = buyingAverage;
                hourlyInfos[i].SmoothedSellingPrice = sellingAverage;
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
                else if (Disabled)
                    return 0.0;

                    return 1.0;
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

                if (Time == now.DateFloorQuarter() && _solarEdgeService.ActualSolarPowerInWatts > minSolarPower)
                    return true;

                return false;
            }
        }

        public bool NetZeroHomeWithoutSolar
        {
            get
            {
                if (Mode == Modes.ZeroNetHome)
                    if(SolarPowerPerQuarterInWatts <= minSolarPower)
                        return true;

                return false;
            }
        }

        /// <summary>
        /// If no (dis)charging is in progress Net Zero Home is requested.
        /// </summary>
        public bool NetZeroHomeWithSolar
        {
            get => (!(Charging || Discharging)) &&
                (
                    NetZeroHomeProfit >= _settingsConfig.NetZeroHomeMinProfit ||
                    SolarPowerPerQuarterInWatts > minSolarPower ||
                    SolarPowerIsActive
                );
        }

        /// <summary>
        /// If no (dis)charging or Zero net home is in progress Sessy's are requested to disable.
        /// </summary>
        public bool Disabled => !(Charging || Discharging || NetZeroHomeWithSolar);

        /// <summary>
        /// The price of energy is negative.
        /// </summary>
        public bool PriceIsNegative => BuyingPrice < 0.0;

        /// <summary>
        /// The buying price of energy is positive.
        /// </summary>
        public bool BuyingPriceIsPositive => BuyingPrice >= 0.0;

        /// <summary>
        /// The selling price of energy is positive.
        /// </summary>
        public bool SellingPriceIsPositive => SellingPrice >= 0.0;

        /// <summary>
        /// For better debugging
        /// </summary>
        public override string ToString()
        {
            return $"{Time}: Charging: {Charging}, Discharging: {Discharging}, Zero Net Home: {NetZeroHomeWithSolar}, Price: {BuyingPrice}, Profit: {Profit}, NZH profit: {NetZeroHomeProfit} Charge left: {_chargeLeft}, Charge needed: {_chargeNeeded}, Solar power {SolarPowerPerQuarterHour}";
        }
    }
}
