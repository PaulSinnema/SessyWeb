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
        /// True when this inverter supports a cloud-based fallback for reading
        /// ActualSolarPowerInWatts when the primary channel is unavailable.
        /// Default: false. Override in provider-specific implementations.
        /// </summary>
        bool SupportsFallback { get; }

        /// <summary>
        /// Returns the current AC power output in Watts via the cloud API fallback.
        /// Only called when IsAvailable is false and SupportsFallback is true.
        /// Implementations should cache the result to avoid exceeding API rate limits.
        /// </summary>
        Task<double> GetFallbackACPowerInWattsAsync();
    }
}