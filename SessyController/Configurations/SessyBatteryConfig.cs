namespace SessyController.Configurations
{
    /// <summary>
    /// This class holds the configurations for all Sessy batteries.
    /// </summary>
    public class SessyBatteryConfig
    {
        public Dictionary<string, SessyBatteryEndpoint>? Batteries { get; set; }
    }
}
