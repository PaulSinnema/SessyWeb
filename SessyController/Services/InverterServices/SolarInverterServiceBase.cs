using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Interfaces;
using Endpoint = SessyCommon.Configurations.Endpoint;

namespace SessyController.Services.InverterServices
{
    /// <summary>
    /// What every solar source shares: its name, its configured endpoints and capacity, and the
    /// reading and availability state SolarInverterManager polls.
    /// </summary>
    public abstract class SolarInverterServiceBase : ISolarInverterService
    {
        protected SolarInverterServiceBase(string providerName, IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor)
        {
            ProviderName = providerName;
            PowerSystemConfig = powerSystemsConfigMonitor.CurrentValue;
            powerSystemsConfigMonitor.OnChange(config => PowerSystemConfig = config);
        }

        /// <summary>The current power systems configuration, refreshed when the file changes.</summary>
        protected PowerSystemsConfig PowerSystemConfig { get; private set; }

        public string ProviderName { get; }

        /// <summary>
        /// This provider's endpoints, empty when it is not configured. SolarInverterManager only
        /// activates services that appear in the config, but the DI container still constructs
        /// every one of them, so an indexer here throws for providers nobody uses.
        /// </summary>
        public Dictionary<string, Endpoint> Endpoints =>
            PowerSystemConfig?.Endpoints != null && PowerSystemConfig.Endpoints.TryGetValue(ProviderName, out var endpoints)
                ? endpoints
                : new Dictionary<string, Endpoint>();

        public double TotalCapacity => Endpoints.Sum(ep => ep.Value.InverterMaxCapacity);

        /// <summary>Most recently measured AC power in Watts, kept current by the source's own loop.</summary>
        public double ActualSolarPowerInWatts { get; protected set; }

        /// <summary>Set by the SolarInverterManager health check.</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>Time of the last successful read, for the health check.</summary>
        public DateTime LastSuccessfulReadUtc { get; protected set; } = DateTime.MinValue;

        /// <summary>No cloud fallback unless a provider adds one.</summary>
        public virtual bool SupportsFallback => false;

        public virtual Task<double> GetFallbackACPowerInWattsAsync() => Task.FromResult(0.0);

        public abstract bool SupportsCurtailment { get; }

        public abstract Task Start(CancellationToken cancelationToken);
        public abstract Task Stop(CancellationToken cancelationToken);
        public abstract Task<double> GetTotalACPowerInWatts();
        public abstract Task<double> GetACPowerInWatts(string id);
        public abstract Task<ushort> GetStatus(string id);
        public abstract Task ThrottleInverterToPercentage(ushort percentage);
    }
}
