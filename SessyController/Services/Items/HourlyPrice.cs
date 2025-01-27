namespace SessyController.Services.Items
{
    public class HourlyPrice
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

        public double ChargeLeftVisual => ChargeLeft / 100000;

        private bool _charging = false;

        /// <summary>
        /// If true charging is requested.
        /// </summary>
        public bool Charging
        {
            get
            {

                return _charging;
            }
            set
            {
                if (value)
                {
                    Discharging = false;
                    CapacityExhausted = false;
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
            set
            {

                if (value)
                {
                    Charging = false;
                    CapacityExhausted = false;
                }

                _discharging = value;
            }
        }

        private bool _capacityExhausted = false;

        /// <summary>
        /// When the capacity for discharging is exhausted, stop (dis)charging and Net Zero Home
        /// </summary>
        public bool CapacityExhausted
        {
            get
            {
                return _capacityExhausted;
            }
            set
            {
                if (value)
                {
                    Charging = false;
                    Discharging = false;
                }

                _capacityExhausted = value;
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
        /// If nog (dis)charging is in progress Net Zero Home is requested.
        /// </summary>
        public bool ZeroNetHome
        {
            get => !(Charging || Discharging || CapacityExhausted);
        }

        /// <summary>
        /// This list contains the hours found for charging against this price.
        /// </summary>
        public List<HourlyPrice>? HoursCharging { get; set; }
    }
}
