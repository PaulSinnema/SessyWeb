using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class SolarHistoryService
    {
        private DbHelper _dbHelper { get; set; }

        public SolarHistoryService(DbHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public void StoreSolarHistoryList(List<SolarHistory> solarHistories)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var solarHistory in solarHistories)
                {
                    if (!db.SolarHistory.Any(sh => sh.Time == solarHistory.Time))
                    {
                        db.SolarHistory.Add(solarHistory);
                    }
                }
            });
        }
    }
}
