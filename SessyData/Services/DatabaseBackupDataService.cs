using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;

namespace SessyData.Services
{
    public class DatabaseBackupDataService
    {
        private IServiceScope _scope { get; set; }
        private DbHelper _dbHelper { get; set; }

        public DatabaseBackupDataService(IServiceScopeFactory serviceScopeFactory)
        {
            _scope = serviceScopeFactory.CreateScope();
            _dbHelper = _scope.ServiceProvider.GetRequiredService<DbHelper>();
        }

        public async Task BackupDatabase()
        {
            await _dbHelper.BackupDatabase();
        }
    }
}
