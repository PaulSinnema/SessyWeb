namespace SessyController.Interfaces
{
    public interface ISolarInverterService
    {
        Task Start(CancellationToken cancelationToken);
        Task<double> GetTotalACPowerInWatts();
        Task<double> GetACPowerInWatts(string id);
        Task<ushort> GetACPower(string id);
        Task<short> GetACPowerScaleFactor(string id);
        Task<ushort> GetStatus(string id);
        string ProviderName { get; }
    }
}
