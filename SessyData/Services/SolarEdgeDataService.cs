using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class SolarEdgeDataService : ServiceBase<SolarInverterData>
    {
        public SolarEdgeDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public async Task RemoveWrongData(string providerName)
        {
            await _dbHelper.ExecuteTransaction((db) =>
            {
                var itemsToRemove = db.Set<SolarInverterData>().Where(x => x.ProviderName == providerName && x.Power > 1000000).ToList();

                if (itemsToRemove.Any())
                {
                    db.Set<SolarInverterData>().RemoveRange(itemsToRemove);
                }
            });
        }
    }
}
