namespace SessyController.Configurations
{
    /// <summary>
    /// This class holds the configurations for all Modbus devices.
    /// </summary>
    public class ModbusConfig
    {
        public Dictionary<string, ModbusEndpoint>? Endpoints { get; set; }
    }
}
