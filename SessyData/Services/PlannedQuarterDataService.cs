using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services;

public class PlannedQuarterDataService : ServiceBase<PlannedQuarter>
{
    public PlannedQuarterDataService(IServiceScopeFactory serviceScopeFactory)
        : base(serviceScopeFactory) { }
}