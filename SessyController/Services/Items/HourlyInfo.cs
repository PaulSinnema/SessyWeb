using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class HourlyInfo
    {
        /// <summary>
        /// Price from ENTSO-E
        /// </summary>
        public double Price { get; set; }

        /// <summary>
        /// Timestamp from ENTSO-E
        /// </summary>
        public DateTime Time { get; set; }

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

        private bool _charging = false;

        public double SolarGlobalRadiation { get; set; }

        public double SolarPower { get; set; }

        public double SolarPowerVisual => SolarPower / 30;

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


        /// <summary>
        /// If true discharging is requested.
        /// </summary>
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
            Charging = mode == Modes.Charging;
            Discharging = mode == Modes.Discharging;
        }

        /// <summary>
        /// Helper for visualization
        /// </summary>
        public double VisualizeInChart
        {
            get
            {
                if (Charging)
                    return -0.03;
                else if (Discharging)
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
                          "Disabled";
            }
        }

        /// <summary>
        /// If no (dis)charging is in progress Net Zero Home is requested.
        /// </summary>
        public bool ZeroNetHome
        {
            get => !(Charging || Discharging);
        }

        /// <summary>
        /// This list contains the hours found for charging against this price.
        /// </summary>
        public List<HourlyInfo>? HoursCharging { get; set; }
    }
}
