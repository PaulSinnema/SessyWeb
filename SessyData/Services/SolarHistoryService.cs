using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class SolarHistoryService
    {
        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        private DbHelper _dbHelper { get; set; }

        public SolarHistoryService(IServiceScopeFactory serviceScopeFactory, DbHelper dbHelper)
        {
            _serviceScopeFactory = serviceScopeFactory;
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
