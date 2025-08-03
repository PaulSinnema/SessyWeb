using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class SolarEdgeDataService : ServiceBase<SolarInverterData>
    {
        public SolarEdgeDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public void RemoveWrongData()
        {
            _dbHelper.ExecuteTransaction((db) =>
            {
                var itemsToRemove = db.Set<SolarInverterData>().Where(x => x.Power > 1000000).ToList();

                if (itemsToRemove.Any())
                {
                    db.Set<SolarInverterData>().RemoveRange(itemsToRemove);
                }
            });
        }
    }
}
