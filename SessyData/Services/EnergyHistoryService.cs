using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class EnergyHistoryService : ServiceBase<EnergyHistory>
    {
        public EnergyHistoryService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public (bool noData, double watts) GetNetPowerBetween(DateTime time, DateTime dateTime)
        {
            var list = _dbHelper.ExecuteQuery(db =>
            {
                return db.Set<EnergyHistory>()
                    .Where(c => c.Time >= time && c.Time <= dateTime)
                    .OrderBy(c => c.Time)
                    .ToList();
            });

            EnergyHistory? previousHistory = null;

            foreach (var energyHistory in list)
            {
                if(previousHistory != null)
                {
                    var consumedTariff1 = energyHistory.ConsumedTariff1 - previousHistory.ConsumedTariff1;
                    var consumedTariff2 = energyHistory.ConsumedTariff2 - previousHistory.ConsumedTariff2;
                    var producedTariff1 = energyHistory.ProducedTariff1 - previousHistory.ProducedTariff1;
                    var producedTariff2 = energyHistory.ProducedTariff2 - previousHistory.ProducedTariff2;

                    return (false , producedTariff1 + producedTariff2 - consumedTariff1 - consumedTariff2);
                }

                previousHistory = energyHistory;
            }

            return (true, 0.0);
        }
    }
}
