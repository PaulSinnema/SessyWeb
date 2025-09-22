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

        public DbHelper(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
            using var scope = _serviceScopeFactory.CreateScope();
        }

        public static bool IsRunningInDocker()
        {
            return File.Exists("/run/.dockerenv");
        }

        private SemaphoreSlim dbHelperSemaphore = new SemaphoreSlim(1);

        public async Task BackupDatabase()
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var timeZoneService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
                var settingsConfig = scope.ServiceProvider.GetRequiredService<IOptions<SettingsConfig>>().Value;

                var now = timeZoneService.Now;

                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();
                var filename = $"Sessy_{now.Year:D4}_{now.Month:D2}_{now.Day:D2}_{now.Hour:D2}_{now.Minute:D2}_{now.Second:D2}.bak";
                var directory = (IsRunningInDocker() ? "" : ".") + settingsConfig.DatabaseBackupDirectory ?? "/data/backups";
                var backupFilePath = Path.Combine(directory, filename).Replace("\\", "/");

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

        public async Task ExecuteTransaction(Func<ModelContext, Task> func)
        {
            await dbHelperSemaphore.WaitAsync();

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();
                await using var transaction = await dbContext.Database.BeginTransactionAsync();

                try
                {
                    await func(dbContext);

                    if (dbContext.ChangeTracker.HasChanges())
                    {
                        var rows = await dbContext.SaveChangesAsync();

                        if (rows == 0)
                            throw new InvalidOperationException($"No rows written to the DB");

                        await transaction.CommitAsync();
                    }
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
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
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();

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
                _isDisposed = true;
            }
        }
    }
}
