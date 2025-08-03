using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class ConsumptionDataService : ServiceBase<Consumption>
    {
        public ConsumptionDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public (bool noData, double watts) GetConsumptionBetween(DateTime time, DateTime dateTime)
        {
            var list = _dbHelper.ExecuteQuery(db =>
            {
                return db.Set<Consumption>()
                    .Where(c => c.Time >= time && c.Time < dateTime)
                    .ToList();
            });

            return ( list.Count <= 0, list.Sum(c => c.ConsumptionWh));
        }

        public void RemoveWrongData()
        {
            _dbHelper.ExecuteTransaction((db) =>
            {
                var itemsToRemove = db.Set<Consumption>().Where(x => x.ConsumptionWh > 1000000).ToList();

                if (itemsToRemove.Any())
                {
                    db.Set<Consumption>().RemoveRange(itemsToRemove);
                }
            });
        }
    }
}
