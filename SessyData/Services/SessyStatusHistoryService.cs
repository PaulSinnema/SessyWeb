using Microsoft.EntityFrameworkCore;
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

        public List<SessyStatusHistory> GetSessyStatusHistory(Func<ModelContext, List<SessyStatusHistory>> func)
        {
            return _dbHelper.ExecuteQuery((ModelContext dbContext) =>
            {
                return func(dbContext);
            });
        }
    }
}
