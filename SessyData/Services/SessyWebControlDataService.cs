using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class SessyWebControlDataService : ServiceBase<SessyWebControl>
    {
        public SessyWebControlDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}
