namespace SessyCommon.Configurations
{
    /// <summary>
    /// This class holds the configurations for all Sessy batteries.
    /// </summary>
    public class SessyBatteryConfig
    {
        /// <summary>Empty rather than null when the section is absent — see PowerSystemsConfig.</summary>
        public Dictionary<string, SessyBatteryEndpoint> Batteries { get; set; } = new();

        /// <summary>
        /// The entries that describe a real device. Everything here sums over this rather than over
        /// Batteries: a credentials-only leftover in secrets.json must not add capacity.
        /// </summary>
        public IEnumerable<KeyValuePair<string, SessyBatteryEndpoint>> ConfiguredBatteries =>
            Batteries.Where(bat => bat.Value.IsConfigured);

        /// <summary>
        /// Total effective charging capacity across all batteries in Watts.
        /// Takes ChargingEfficiencyFactor per battery into account.
        /// </summary>
        public double TotalChargingCapacity => ConfiguredBatteries.Sum(bat => bat.Value.EffectiveMaxCharge);

        /// <summary>
        /// Raw maximum charge capacity without any efficiency factor applied.
        /// Use this when an override factor is applied externally (e.g. from Settings DB).
        /// </summary>
        public double TotalRawChargingCapacity => ConfiguredBatteries.Sum(bat => bat.Value.MaxCharge);

        /// <summary>
        /// Total effective discharging capacity across all batteries in Watts.
        /// Takes DischargingEfficiencyFactor per battery into account.
        /// </summary>
        public double TotalDischargingCapacity => ConfiguredBatteries.Sum(bat => bat.Value.EffectiveMaxDischarge);

        /// <summary>
        /// Raw maximum discharge capacity without any efficiency factor applied.
        /// Use this when an override factor is applied externally (e.g. from Settings DB).
        /// </summary>
        public double TotalRawDischargingCapacity => ConfiguredBatteries.Sum(bat => bat.Value.MaxDischarge);

        /// <summary>
        /// Total energy capacity across all batteries in Wh.
        /// </summary>
        public double TotalCapacity => ConfiguredBatteries.Sum(bat => bat.Value.Capacity);
    }
}