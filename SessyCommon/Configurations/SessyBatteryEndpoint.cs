namespace SessyCommon.Configurations
{
    /// <summary>
    /// This class holds the information for 1 Sessy battery.
    /// </summary>
    public class SessyBatteryEndpoint
    {
        /// <summary>
        /// A custom name for the battery.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Userid from the installation card.
        /// </summary>
        public string? UserId { get; set; }

        /// <summary>
        /// Password from the installation card.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// Base url where the API can be found of the battery.
        /// </summary>
        public string? BaseUrl { get; set; }

        /// <summary>
        /// The maximum charging capacity.
        /// </summary>
        public double MaxCharge { get; set; }

        /// <summary>
        /// The maximum discharging capacity.
        /// </summary>
        public double MaxDischarge { get; set; }

        /// <summary>
        /// Amount of charge the battery can hold.
        /// </summary>
        public double Capacity { get; set; }
    }
}
