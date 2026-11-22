namespace SessyCommon.Configurations
{
    /// <summary>
    /// This class holds the information for 1 Sessy battery.
    /// </summary>
    public class SessyBatteryEndpoint : SessyDeviceEndpoint
    {

        /// <summary>
        /// The maximum charging capacity in Watts.
        /// Default: 1400W. Falls back to 1400W when not configured or invalid.
        /// </summary>
        public double MaxCharge
        {
            get => _maxCharge;
            set => _maxCharge = value <= 0.0 ? 1400.0 : value;
        }
        private double _maxCharge = 1400.0;

        /// <summary>
        /// The maximum discharging capacity in Watts.
        /// Default: 1400W. Falls back to 1400W when not configured or invalid.
        /// </summary>
        public double MaxDischarge
        {
            get => _maxDischarge;
            set => _maxDischarge = value <= 0.0 ? 1400.0 : value;
        }
        private double _maxDischarge = 1400.0;

        /// <summary>
        /// Amount of charge the battery can hold in Wh.
        /// </summary>
        public double Capacity { get; set; }

        /// <summary>
        /// Factor applied to MaxCharge to account for thermal throttling.
        /// The Sessy automatically reduces charging power when the battery
        /// gets too warm. Set this to the expected real-world charging fraction.
        /// Range: 0.0 – 1.0. Default: 0.80 (20% reduction as conservative estimate).
        /// Example: 0.80 means the effective charging capacity is 80% of MaxCharge.
        /// </summary>
        public double ChargingEfficiencyFactor
        {
            get => _chargingEfficiencyFactor;
            set => _chargingEfficiencyFactor = (value <= 0.0 || value > 1.0) ? 0.80 : value;
        }
        private double _chargingEfficiencyFactor = 0.80;

        /// <summary>
        /// Factor applied to MaxDischarge to account for thermal throttling.
        /// Range: 0.0 – 1.0. Default: 0.80 (20% reduction as conservative estimate).
        /// Example: 0.80 means the effective discharging capacity is 80% of MaxDischarge.
        /// </summary>
        public double DischargingEfficiencyFactor
        {
            get => _dischargingEfficiencyFactor;
            set => _dischargingEfficiencyFactor = (value <= 0.0 || value > 1.0) ? 0.80 : value;
        }
        private double _dischargingEfficiencyFactor = 0.80;

        /// <summary>
        /// Effective maximum charging capacity after applying the efficiency factor.
        /// </summary>
        public double EffectiveMaxCharge => MaxCharge * ChargingEfficiencyFactor;

        /// <summary>
        /// Effective maximum discharging capacity after applying the efficiency factor.
        /// </summary>
        public double EffectiveMaxDischarge => MaxDischarge * DischargingEfficiencyFactor;
    }
}