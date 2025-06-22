using SessyController.Interfaces;

namespace SessyController.Services.InverterServices
{
    public class HuaweiInverterService : ISolarInverterService
    {
        public string ProviderName => "Huawei";

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task Start(CancellationToken cancelationToken)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
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
