
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using System.Threading;

namespace SessyController.Services
{
    public class DatabaseBackupService : BackgroundService
    {
        private LoggingService<DatabaseBackupService> _logger { get; set; }

        private TimeZoneService _timeZoneService { get; set; }

        private DatabaseBackupDataService _databaseBackupDataService { get; set; }

        private NotificationService _notificationService { get; set; }

        public DatabaseBackupService(LoggingService<DatabaseBackupService> logger,
                                     TimeZoneService timeZoneService,
                                     DatabaseBackupDataService databaseBackupDataService,
                                     NotificationService notificationService)
        {
            _logger = logger;
            _timeZoneService = timeZoneService;
            _databaseBackupDataService = databaseBackupDataService;
            _notificationService = notificationService;
        }

        // ── Backup health (surfaced in Tips & Checks) ─────────────────────────
        // The nightly backup swallows its own exceptions into the log, so without these a broken
        // backup stays invisible until someone notices a stale .bak file weeks later.
        public DateTime? LastBackupAttemptAt { get; private set; }
        public DateTime? LastBackupSuccessAt { get; private set; }
        public string? LastBackupError { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("Database Backup Service started ...");

            // await TemporaryRemoveAllNoneWholeHours();

            // Loop to fetch prices every day
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    var now = _timeZoneService.Now;
                    var tomorrow = now.Date.AddDays(1);
                    var minutes = (tomorrow - now).TotalMinutes; // Wait until the next day starts

                    await Task.Delay(TimeSpan.FromMinutes(minutes), cancelationToken);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay, keep processing {ex.ToDetailedString()}");
                }

                try
                {
                    await BackupNowAsync();
                }
                catch (Exception ex)
                {
                    // BackupNowAsync already raised the failure notification; just log here.
                    _logger.LogException(ex, "An error occurred while processing Database Backup.");
                }
            }

            _logger.LogWarning("Database Backup service stopped.");
        }

        /// <summary>
        /// Runs a backup and raises the matching notification. This is the single place both the
        /// nightly run and the manual Settings button go through, so the notification logic lives
        /// with the routine that actually performs the backup rather than being repeated per caller.
        /// Rethrows on failure so a caller can still surface the error in its own UI.
        /// </summary>
        public async Task<string> BackupNowAsync()
        {
            LastBackupAttemptAt = _timeZoneService.Now;

            try
            {
                var path = await _databaseBackupDataService.BackupDatabase();

                LastBackupSuccessAt = _timeZoneService.Now;
                LastBackupError = null;

                // Clear any earlier failures and record a single, stable success entry.
                await _notificationService.ClearByKeyAsync("backup-failed");
                await _notificationService.AddAsync(
                    NotificationSeverity.Information, "Backup", "Database backup succeeded",
                    "The database was backed up successfully.", dedupKey: "backup-ok");

                return path;
            }
            catch (Exception ex)
            {
                // Show the real underlying cause, not the wrapper. A different reason produces a
                // separate notification (identity is key + message); an identical repeat increments.
                var reason = ex.RootMessage();

                LastBackupError = reason;

                await _notificationService.AddAsync(
                    NotificationSeverity.Error, "Backup", "Database backup failed",
                    reason, dedupKey: "backup-failed");
                await _notificationService.ClearByKeyAsync("backup-ok");

                throw;
            }
        }

        // Superseded by BackupNowAsync; kept as dead code rather than re-figuring it out later.
        private async Task<string> Process(object cancelationToken)
        {
            return await _databaseBackupDataService.BackupDatabase();
        }
    }
}
