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
    }
}
