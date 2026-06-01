namespace SessyCommon.Configurations
{
    /// <summary>
    /// This class holds the configurations for all Sessy batteries.
    /// </summary>
    public class SessyBatteryConfig
    {
        public Dictionary<string, SessyBatteryEndpoint>? Batteries { get; set; }

        /// <summary>
        /// Total effective charging capacity across all batteries in Watts.
        /// Takes ChargingEfficiencyFactor per battery into account.
        /// </summary>
        public double TotalChargingCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.EffectiveMaxCharge);

        /// <summary>
        /// Raw maximum charge capacity without any efficiency factor applied.
        /// Use this when an override factor is applied externally (e.g. from Settings DB).
        /// </summary>
        public double TotalRawChargingCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.MaxCharge);

        /// <summary>
        /// Total effective discharging capacity across all batteries in Watts.
        /// Takes DischargingEfficiencyFactor per battery into account.
        /// </summary>
        public double TotalDischargingCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.EffectiveMaxDischarge);

        /// <summary>
        /// Raw maximum discharge capacity without any efficiency factor applied.
        /// Use this when an override factor is applied externally (e.g. from Settings DB).
        /// </summary>
        public double TotalRawDischargingCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.MaxDischarge);

        /// <summary>
        /// Total energy capacity across all batteries in Wh.
        /// </summary>
        public double TotalCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.Capacity);
    }
}