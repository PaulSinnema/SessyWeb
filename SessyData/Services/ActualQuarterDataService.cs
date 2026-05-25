using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services;

public class ActualQuarterDataService : ServiceBase<ActualQuarter>
{
    public ActualQuarterDataService(IServiceScopeFactory serviceScopeFactory)
        : base(serviceScopeFactory) { }
}