namespace SessyCommon.Configurations
{
    /// <summary>
    /// This class holds the configurations for all interfaces.
    /// </summary>
    public class PowerSystemsConfig
    {
        /// <summary>
        /// Empty rather than null when the section is absent: a household without solar panels
        /// simply has no PowerSystems section, and every consumer here iterates or looks up. It
        /// used to be null only in theory, because the appsettings.json baked into the image
        /// always filled the section in — until v1.0.78 stopped shipping it.
        /// </summary>
        public Dictionary<string, Dictionary<string, Endpoint>> Endpoints { get; set; } = new();

        /// <summary>
        /// True when at least one inverter is configured. The single definition of "this household
        /// has solar" — the UI hides solar pages and figures on it, so it must not be restated
        /// anywhere else.
        /// </summary>
        public bool HasSolar => Endpoints.Values.Any(inverters => inverters.Count > 0);
    }
}
