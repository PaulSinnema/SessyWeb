namespace SessyCommon.Configurations
{
    /// <summary>
    /// This class holds the configurations for all P1 meters (should be only 1).
    /// </summary>
    public class SessyP1Config
    {
        /// <summary>Empty rather than null when the section is absent — see PowerSystemsConfig.</summary>
        public Dictionary<string, SessyP1Endpoint> Endpoints { get; set; } = new();
    }
}
