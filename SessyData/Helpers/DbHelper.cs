using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyData.Model;

namespace SessyData.Helpers
{

    public class DbHelper : IDisposable
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private IServiceScope _scope { get; set; }

        private TimeZoneService _timeZoneService { get; set; }
        private SettingsConfig _settingsConfig { get; set; }

        public DbHelper(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _scope = _serviceScopeFactory.CreateScope();

            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _settingsConfig = _scope.ServiceProvider.GetRequiredService<IOptions<SettingsConfig>>().Value;
        }

        private SemaphoreSlim dbHelperSemaphore = new SemaphoreSlim(1);

        public async Task BackupDatabase()
        {
            try
            {
                var now = _timeZoneService.Now;
                var dbContext = _scope.ServiceProvider.GetRequiredService<ModelContext>();
                var filename = $"Sessy_{now.Year}_{now.Month}_{now.Day}_{now.Hour}_{now.Minute}_{now.Second}.bak";
                var backupFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename).Replace("\\", "/");

                // Ensure the backup directory exists
                var directory = Path.GetDirectoryName(backupFilePath) ?? throw new InvalidOperationException("Backup directory path is invalid.");

                Directory.CreateDirectory(directory);

                if (!Directory.Exists(directory))
                    throw new InvalidOperationException($"Backup directory does not exist: {directory}");

                // Perform the backup operation
                await ExecuteQuery(async (db) =>
                {
                    FormattableString sql = @$"VACUUM INTO {backupFilePath}";

                    Console.WriteLine("Issuing SQL Command: " + sql);

                    await db.Database.ExecuteSqlAsync(sql);
                });

                Console.WriteLine($"Database backup completed successfully to {backupFilePath}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database backup failed: {ex.ToDetailedString()}", ex);
            }
        }

        public async Task ExecuteTransaction(Action<ModelContext> action)
        {
            await dbHelperSemaphore.WaitAsync();

            try
            {
                var dbContext = _scope.ServiceProvider.GetRequiredService<ModelContext>();
                using var transaction = dbContext.Database.BeginTransaction();

                try
                {
                    action(dbContext);
                    dbContext.SaveChanges();
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new InvalidOperationException($"Database transaction failed: {ex.ToDetailedString()}", ex);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database operation error: {ex.ToDetailedString()}", ex);
            }
            finally
            {
                dbHelperSemaphore.Release();
            }
        }

        public T ExecuteQuery<T>(Func<ModelContext, T> queryFunc)
        {
            dbHelperSemaphore.Wait();

            try
            {
                var dbContext = _scope.ServiceProvider.GetRequiredService<ModelContext>();

                return queryFunc(dbContext);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database query error: {ex.Message}", ex);
            }
            finally
            {
                dbHelperSemaphore.Release();
            }
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _scope.Dispose();
            }
        }
    }
}
