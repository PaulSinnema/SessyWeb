using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class SessyStatusHistoryService : ServiceBase<SessyStatusHistory>
    {
        public SessyStatusHistoryService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public async Task<List<GroupedSessyStatus>> GetSessyStatusHistory(Func<ModelContext, List<GroupedSessyStatus>> func)
        {
            return await _dbHelper.ExecuteQueryAsync((ModelContext dbContext) =>
            {
                return func(dbContext);
            }).ConfigureAwait(false);
        }
    }
}
