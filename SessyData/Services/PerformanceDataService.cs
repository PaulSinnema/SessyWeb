using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class PerformanceDataService : ServiceBase<Performance>
    {
        public PerformanceDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}
