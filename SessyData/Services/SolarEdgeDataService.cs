using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class SolarEdgeDataService : ServiceBase<SolarEdgeData>
    {
        public SolarEdgeDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}
