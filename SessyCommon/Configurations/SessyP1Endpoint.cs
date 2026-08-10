namespace SessyCommon.Configurations
{
    public class SessyP1Endpoint
    {
        public string? Name { get; set; }
        public string? UserId { get; set; }
        public string? Password { get; set; }
        public string? BaseUrl { get; set; }

        /// <summary>True when this entry describes a real device — see SessyBatteryEndpoint.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
    }
}