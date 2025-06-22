using SessyController.Interfaces;

namespace SessyController.Services
{
    public class SolisInverterService : ISolarInverterService
    {
        public string ProviderName => "Solis";

        public Task Start(CancellationToken cancelationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ushort> GetACPower(string id)
        {
            throw new NotImplementedException();
        }

        public Task<double> GetACPowerInWatts(string id)
        {
            throw new NotImplementedException();
        }

        public Task<short> GetACPowerScaleFactor(string id)
        {
            throw new NotImplementedException();
        }

        public Task<ushort> GetStatus(string id)
        {
            throw new NotImplementedException();
        }

        public Task<double> GetTotalACPowerInWatts()
        {
            throw new NotImplementedException();
        }
    }
}
