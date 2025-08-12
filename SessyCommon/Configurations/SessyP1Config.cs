namespace SessyCommon.Configurations
{
    /// <summary>
    /// This class holds the configurations for all P1 meters (should be only 1).
    /// </summary>
    public class SessyP1Config
    {
        public Dictionary<string, SessyP1Endpoint>? Endpoints { get; set; }
    }
}
