using SessyData.Helpers;
using SessyData.Model;

namespace SessyData.Services
{
    class SessyStatusHistoryService
    {
        private DbHelper _dbHelper { get; set; }

        public SessyStatusHistoryService(DbHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public void SessyStatusHistoryList(List<SessyStatusHistory> statusHistories)
        {
            _dbHelper.ExecuteTransaction(db =>
            {
                foreach (var statusHistory in statusHistories)
                {
                    db.SessyStatusHistory.Add(statusHistory);
                }
            });
        }
    }
}
