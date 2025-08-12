
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

        public DatabaseBackupService(LoggingService<DatabaseBackupService> logger,
                                     TimeZoneService timeZoneService,
                                     DatabaseBackupDataService databaseBackupDataService)
        {
            _logger = logger;
            _timeZoneService = timeZoneService;
            _databaseBackupDataService = databaseBackupDataService;
        }

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
                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while processing Database Backup.");
                }
            }

            _logger.LogWarning("Database Backup service stopped.");
        }

        private async Task Process(object cancelationToken)
        {
            await _databaseBackupDataService.BackupDatabase();
        }
    }
}
