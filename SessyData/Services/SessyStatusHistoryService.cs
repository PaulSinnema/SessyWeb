using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class SessyStatusHistoryService : ServiceBase<SessyStatusHistory>
    {
        public SessyStatusHistoryService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public List<GroupedSessyStatus> GetSessyStatusHistory(Func<ModelContext, List<GroupedSessyStatus>> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext dbContext) =>
            {
                return func(dbContext);
            });
        }
    }
}
