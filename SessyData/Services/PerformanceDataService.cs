using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class PerformanceDataService : ServiceBase<Performance>
    {
        public PerformanceDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public async Task RemoveWrongData()
        {
            var date = new DateTime(2026, 5, 5);

            await _dbHelper.ExecuteTransaction(async db =>
            {
                var itemsToRemove = db.Set<Performance>().Where(x => x.SolarPowerPerQuarterHour > 1).ToList();

                //if (itemsToRemove.Any())
                //{
                //    db.Set<Performance>().RemoveRange(itemsToRemove);
                //}

                await Task.FromResult<bool>(true);
            });
        }
    }
}
