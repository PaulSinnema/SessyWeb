using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services;

public class ForecastSnapshotDataService : ServiceBase<ForecastSnapshot>
{
    public ForecastSnapshotDataService(IServiceScopeFactory serviceScopeFactory)
        : base(serviceScopeFactory) { }
}
