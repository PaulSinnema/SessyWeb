using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class EnergyHistoryService
    {
        private DbHelper _dbHelper { get; set; }

        public EnergyHistoryService(DbHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public void StoreSessyStatusHistoryList(List<EnergyHistory> energyHistories)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var energyHistory in energyHistories)
                {
                    db.EnergyHistory.Add(energyHistory);
                }
            });
        }

        public EnergyHistory? GetEnergyHistory(Func<ModelContext, EnergyHistory?> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext dbContext) =>
            {
                return func(dbContext);
            });
        }

        public List<EnergyHistory> GetEnergyHistoryList(Func<ModelContext, List<EnergyHistory>> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext dbContext) =>
            {
                return func(dbContext);
            });
        }
    }
}
