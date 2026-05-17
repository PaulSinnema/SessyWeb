using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class InvestmentGroupDataService : ServiceBase<InvestmentGroup>
    {
        public InvestmentGroupDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory)
        {
        }
    }
}