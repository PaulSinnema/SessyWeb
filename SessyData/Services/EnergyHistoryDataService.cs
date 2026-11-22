using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class EnergyHistoryDataService : ServiceBase<EnergyHistory>
    {
        public EnergyHistoryDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

    }
}
