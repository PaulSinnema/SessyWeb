using Endpoint = SessyController.Configurations.Endpoint;

namespace SessyController.Interfaces
{
    public interface ISolarInverterService
    {
        Task Start(CancellationToken cancelationToken);
        Task Stop(CancellationToken cancelationToken);
        Task<double> GetTotalACPowerInWatts();
        Task<double> GetACPowerInWatts(string id);
        Task<ushort> GetACPower(string id);
        Task<short> GetACPowerScaleFactor(string id);
        Task<ushort> GetStatus(string id);
        Task ThrottleInverterToPercentage(ushort percentage);

        Dictionary<string, Endpoint> Endpoints { get; }
        string ProviderName { get; }
    }
}
