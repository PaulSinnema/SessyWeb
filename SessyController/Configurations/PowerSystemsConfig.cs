using SessyController.Services.Items;

namespace SessyController.Configurations
{
    /// <summary>
    /// This class holds the configurations for all interfaces.
    /// </summary>
    public class PowerSystemsConfig
    {
        public Dictionary<string, Endpoint>? Endpoints { get; set; }

    }
}
