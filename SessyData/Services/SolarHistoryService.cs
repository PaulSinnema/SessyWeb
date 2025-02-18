using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class SolarHistoryService : ServiceBase<SolarHistory>
    {
        public SolarHistoryService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}
