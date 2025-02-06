using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    public class SessyStatusHistoryService
    {
        private DbHelper _dbHelper { get; set; }

        public SessyStatusHistoryService(DbHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public void StoreSessyStatusHistoryList(List<SessyStatusHistory> statusHistories)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var statusHistory in statusHistories)
                {
                    db.SessyStatusHistory.Add(statusHistory);
                }
            });
        }

        public List<SessyStatusHistory> GetSessyStatusHistory(DateTime startDate, int count)
        {
            return _dbHelper.ExecuteQuery<List<SessyStatusHistory>>((ModelContext dbContext) =>
            {
                return dbContext.SessyStatusHistory
                    .OrderBy(ssh => ssh.Time)
                    .Where(ssh => ssh.Time >= startDate)
                    .Take(count)
                    .ToList();
            });
        }
    }
}
