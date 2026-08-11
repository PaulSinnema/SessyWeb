using Endpoint = SessyCommon.Configurations.Endpoint;

namespace SessyController.Interfaces
{
    public interface ISolarInverterService
    {
        Task Start(CancellationToken cancelationToken);
        Task Stop(CancellationToken cancelationToken);
        Task<double> GetTotalACPowerInWatts();
        Task<double> GetACPowerInWatts(string id);
        Task<ushort> GetStatus(string id);
        Task ThrottleInverterToPercentage(ushort percentage);
        double ActualSolarPowerInWatts { get; }
        Dictionary<string, Endpoint> Endpoints { get; }
        string ProviderName { get; }
        double TotalCapacity { get; }

        /// <summary>
        /// True when the inverter is reachable via its primary communication channel
        /// (e.g. Modbus TCP). Updated periodically by the health check in SolarInverterManager.
        /// </summary>
        bool IsAvailable { get; set; }

        /// <summary>
        /// Timestamp of the last successful Modbus read in UTC.
        /// Used by SolarInverterManager health check to determine availability
        /// without making a new Modbus connection that would conflict with the
        /// Process() loop reading every second.
        /// </summary>
        DateTime LastSuccessfulReadUtc { get; }

        /// <summary>
        /// True when this inverter supports a cloud-based fallback for reading
        /// ActualSolarPowerInWatts when the primary channel is unavailable.
        /// Default: false. Override in provider-specific implementations.
        /// </summary>
        bool SupportsFallback { get; }

        /// <summary>
        /// True when ThrottleInverterToPercentage actually reaches the hardware. A read-only source
        /// measures production but cannot reduce it, and curtailment at negative prices must not
        /// silently do nothing — worse, FORCE_CHARGE charges from the grid at full power precisely
        /// because it assumes the inverter has been shut down.
        ///
        /// This is a capability, not a reachability check: an offline Modbus inverter still reports
        /// true, so nothing about the existing behaviour changes.
        /// </summary>
        bool SupportsCurtailment { get; }

        /// <summary>
        /// Returns the current AC power output in Watts via the cloud API fallback.
        /// Only called when IsAvailable is false and SupportsFallback is true.
        /// Implementations should cache the result to avoid exceeding API rate limits.
        /// </summary>
        Task<double> GetFallbackACPowerInWattsAsync();
    }
}