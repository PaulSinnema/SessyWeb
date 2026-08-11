using SessyCommon.Services.Items;

namespace SessyCommon.Configurations
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

        public double InverterMaxCapacity { get; set; }

        /// <summary>
        /// Sessy source only: which batteries carry the CT clamps around the PV group, by their key
        /// in Sessy:Batteries:Batteries. Absent or empty means all of them.
        ///
        /// Only needed when several Sessys see the SAME clamps — then adding them up counts the same
        /// production twice. A battery without clamps reports 0 W and adds nothing, so the default is
        /// safe for the usual wiring. Battery keys, not addresses: those stay in Sessy:Batteries.
        /// </summary>
        public List<string>? Batteries { get; set; }

        public Dictionary<string, PhotoVoltaic>? SolarPanels { get; set; }
    }
}
