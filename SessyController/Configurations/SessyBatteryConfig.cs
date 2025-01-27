namespace SessyController.Configurations
{
    /// <summary>
    /// This class holds the configurations for all Sessy batteries.
    /// </summary>
    public class SessyBatteryConfig
    {
        public Dictionary<string, SessyBatteryEndpoint>? Batteries { get; set; }

        public double TotalChargingCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.MaxCharge);
        public double TotalDischargingCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.MaxDischarge);
        public double TotalCapacity => Batteries == null ? 0 : Batteries.Sum(bat => bat.Value.Capacity);
    }
}
