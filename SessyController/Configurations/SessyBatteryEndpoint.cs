namespace SessyController.Configurations
{
    /// <summary>
    /// This class holds the information for 1 Sessy battery.
    /// </summary>
    public class SessyBatteryEndpoint
    {
        public string? Name { get; set; }
        public string? UserId { get; set; }
        public string? Password { get; set; }
        public string? BaseUrl { get; set; }
        public int MaxCharge { get; set; }
        public int MaxDischarge { get; set; }
        public double Capacity { get; set; }
    }
}
