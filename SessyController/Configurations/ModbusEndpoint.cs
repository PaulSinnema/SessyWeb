namespace SessyController.Configurations
{
    /// <summary>
    /// This class holds information for 1 modus configuration.
    /// </summary>
    public class ModbusEndpoint
    {
        public string? IpAddress { get;set; }
        public int Port { get;set; }
        public byte SlaveId { get;set; }
    }
}
