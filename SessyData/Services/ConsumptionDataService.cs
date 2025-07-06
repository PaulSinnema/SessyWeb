using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class ConsumptionDataService : ServiceBase<Consumption>
    {
        public ConsumptionDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}
