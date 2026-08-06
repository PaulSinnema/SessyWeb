using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class ConsumptionDataService : ServiceBase<Consumption>
    {
        public ConsumptionDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public async Task<(bool noData, double watts)> GetConsumptionBetween(DateTime time, DateTime dateTime)
        {
            var list = await _dbHelper.ExecuteQueryAsync(db =>
            {
                return db.Set<Consumption>()
                    .Where(c => c.Time >= time && c.Time < dateTime)
                    .ToList();
            }).ConfigureAwait(false);

            return ( list.Count <= 0, list.Sum(c => c.ConsumptionWh));
        }
    }
}
