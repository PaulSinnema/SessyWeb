using SessyController.Services.Items;

namespace SessyController.Configurations
{
    /// <summary>
    /// This class holds information for 1 modus configuration.
    /// </summary>
    public class Endpoint
    {
        public string? Interface { get; set; }
        public string? IpAddress { get;set; }
        public int Port { get;set; }
        public byte SlaveId { get;set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double InverterMaxCapacity { get; set; }

        public Dictionary<string, PhotoVoltaic>? SolarPanels { get; set; }
    }
}
