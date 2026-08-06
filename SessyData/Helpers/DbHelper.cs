using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyData.Model;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SessyData.Helpers
{
    /// <summary>
    /// Single entry point for database access: it opens a fresh scope (and thus a fresh
    /// ModelContext) per call and keeps writes serialized.
    ///
    /// Reads and writes are separated on purpose. Writes go one at a time, because SQLite allows
    /// exactly one writer and the retry noise is not worth it. Reads run concurrently, bounded
    /// only so a runaway page cannot open an unlimited number of connections — under WAL a reader
    /// never has to wait for a writer.
    ///
    /// Nothing here blocks a thread any more. The read path used to start with a synchronous
    /// SemaphoreSlim.Wait(), so every query in the application parked a thread-pool thread while it
    /// waited. The pool grows by roughly one thread per second, which is exactly what a UI that
    /// freezes for seconds at a time looks like.
    ///
    /// Thread safety: the SQLite library is thread-safe across CONNECTIONS, not within one, and an
    /// EF DbContext is not thread-safe at all. Every method here therefore opens its own scope and
    /// so its own context and connection, and awaits the caller's delegate inside that scope —
    /// nothing is ever shared between the callers running in parallel.
    /// </summary>
    public class DbHelper : IDisposable
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<DbHelper>? _logger;

        // Two constructors rather than one with an optional argument: dependency injection picks
        // the widest one it can satisfy, while Moq matches a constructor on the exact arguments it
        // is handed and cannot see through an optional parameter.
        public DbHelper(IServiceScopeFactory serviceScopeFactory)
            : this(serviceScopeFactory, null)
        {
        }

        public DbHelper(IServiceScopeFactory serviceScopeFactory, ILogger<DbHelper>? logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        /// <summary>Concurrent readers allowed per data service.</summary>
        private const int MaxConcurrentReads = 4;

        /// <summary>Waiting or holding longer than this is worth a log line — see ReportSlow.</summary>
        private const int SlowWaitMs = 250;
        private const int SlowHoldMs = 500;

        /// <summary>
        /// Static on purpose: SQLite permits exactly one writer per database, and this object is
        /// registered per scope, so a per-instance semaphore would only serialize one entity's
        /// writes. Two data services writing at the same time would then race for the file lock and
        /// the loser gets SQLITE_BUSY after busy_timeout. One process-wide gate keeps that from
        /// happening at all.
        /// </summary>
        private static readonly SemaphoreSlim _writeSemaphore = new(1, 1);

        /// <summary>Per instance: readers do not exclude each other under WAL.</summary>
        private readonly SemaphoreSlim _readSemaphore = new(MaxConcurrentReads, MaxConcurrentReads);

        /// <summary>Writes a VACUUM INTO backup and returns the file it was written to.</summary>
        public async Task<string> BackupDatabase()
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var timeZoneService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
                var settingsConfig = scope.ServiceProvider.GetRequiredService<IOptions<SettingsConfig>>().Value;

                var now = timeZoneService.Now;

                var filename = $"Sessy_{now.Year:D4}_{now.Month:D2}_{now.Day:D2}_{now.Hour:D2}_{now.Minute:D2}_{now.Second:D2}.bak";
                var directory = DockerService.FileName(settingsConfig.DatabaseBackupDirectory ?? "/SessyController/Data/backups");
                var backupFilePath = Path.Combine(directory, filename).Replace("\\", "/");

                Directory.CreateDirectory(directory);

                if (!Directory.Exists(directory))
                    throw new InvalidOperationException($"Backup directory does not exist: {directory}");

                // VACUUM rewrites the whole file, so it takes the write side — but it cannot run
                // inside a transaction, hence ExecuteWriteAsync rather than ExecuteTransaction.
                await ExecuteWriteAsync(async db =>
                {
                    FormattableString sql = @$"VACUUM INTO {backupFilePath}";

                    Console.WriteLine("Issuing SQL Command: " + sql);

                    await db.Database.ExecuteSqlAsync(sql);
                }).ConfigureAwait(false);

                Console.WriteLine($"Database backup completed successfully to {backupFilePath}");

                return backupFilePath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database backup failed: {ex.ToDetailedString()}", ex);
            }
        }

        /// <summary>
        /// Takes the write side without opening a transaction. For statements SQLite refuses to run
        /// inside one, VACUUM being the reason this exists.
        /// </summary>
        public async Task ExecuteWriteAsync(Func<ModelContext, Task> func, [CallerMemberName] string caller = "")
        {
            var waited = Stopwatch.StartNew();
            await _writeSemaphore.WaitAsync().ConfigureAwait(false);
            waited.Stop();

            var held = Stopwatch.StartNew();

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();

                await func(dbContext).ConfigureAwait(false);
            }
            finally
            {
                _writeSemaphore.Release();
                ReportSlow("write", caller, waited.ElapsedMilliseconds, held.ElapsedMilliseconds);
            }
        }

        public async Task ExecuteTransaction(Func<ModelContext, Task> func, [CallerMemberName] string caller = "")
        {
            var waited = Stopwatch.StartNew();
            await _writeSemaphore.WaitAsync().ConfigureAwait(false);
            waited.Stop();

            var held = Stopwatch.StartNew();

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();
                await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);

                try
                {
                    await func(dbContext).ConfigureAwait(false);

                    if (dbContext.ChangeTracker.HasChanges())
                    {
                        var rows = await dbContext.SaveChangesAsync().ConfigureAwait(false);

                        if (rows == 0)
                            throw new InvalidOperationException($"No rows written to the DB");

                        await transaction.CommitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"Database transaction failed: {ex.ToDetailedString()}", ex);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database operation error: {ex.ToDetailedString()}", ex);
            }
            finally
            {
                _writeSemaphore.Release();
                ReportSlow("write", caller, waited.ElapsedMilliseconds, held.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Runs a read against a fresh context. The delegate is awaited INSIDE the scope: the old
        /// synchronous overload returned the caller's Task and disposed the context on the way out,
        /// which only ever worked because the SQLite provider completes almost everything
        /// synchronously.
        /// </summary>
        public async Task<T> ExecuteQueryAsync<T>(Func<ModelContext, Task<T>> queryFunc, [CallerMemberName] string caller = "")
        {
            var waited = Stopwatch.StartNew();
            await _readSemaphore.WaitAsync().ConfigureAwait(false);
            waited.Stop();

            var held = Stopwatch.StartNew();

            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ModelContext>();

                return await queryFunc(dbContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Database query error: {ex.Message}", ex);
            }
            finally
            {
                _readSemaphore.Release();
                ReportSlow("read", caller, waited.ElapsedMilliseconds, held.ElapsedMilliseconds);
            }
        }

        /// <summary>Read with a synchronous delegate — same path, without forcing callers to fake a Task.</summary>
        public Task<T> ExecuteQueryAsync<T>(Func<ModelContext, T> queryFunc, [CallerMemberName] string caller = "")
            => ExecuteQueryAsync(db => Task.FromResult(queryFunc(db)), caller);

        /// <summary>Logs the calls that are slow enough to be felt in the UI.</summary>
        private void ReportSlow(string kind, string caller, long waitedMs, long heldMs)
        {
            if (waitedMs < SlowWaitMs && heldMs < SlowHoldMs) return;

            var message = $"DbHelper: slow {kind} from {caller} — waited {waitedMs} ms, held {heldMs} ms";

            if (_logger != null)
                _logger.LogInformation(message);
            else
                Console.WriteLine(message);
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;

                _readSemaphore.Dispose();   // the write gate is static and outlives this instance
            }
        }
    }
}
