using Microsoft.Extensions.Options;
using SessyCommon.Configurations;

namespace SessyController.Services
{
    /// <summary>
    /// What this installation actually has, so the UI can leave out what does not apply.
    ///
    /// A household without solar panels has no PowerSystems section at all; showing it a Solar
    /// power page and a performance ratio that can only ever read 0% invites bug reports about
    /// figures that are working exactly as intended.
    ///
    /// Read from the monitor on every call rather than cached, so editing appsettings.json takes
    /// effect without a restart — the same reason the services here subscribe to OnChange.
    /// </summary>
    public class SystemCapabilitiesService
    {
        private readonly IOptionsMonitor<PowerSystemsConfig> _powerSystemsConfigMonitor;

        public SystemCapabilitiesService(IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor)
        {
            _powerSystemsConfigMonitor = powerSystemsConfigMonitor;
        }

        /// <summary>True when at least one inverter is configured.</summary>
        public bool HasSolar => _powerSystemsConfigMonitor.CurrentValue.HasSolar;
    }
}
