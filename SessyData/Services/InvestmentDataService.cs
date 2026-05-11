using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class InvestmentDataService : ServiceBase<Investment>
    {
        public InvestmentDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}