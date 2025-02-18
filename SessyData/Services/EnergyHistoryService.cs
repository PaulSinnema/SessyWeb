using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class EnergyHistoryService : ServiceBase<EnergyHistory>
    {
        public EnergyHistoryService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}
