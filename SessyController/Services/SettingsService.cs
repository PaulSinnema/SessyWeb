using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Single source of truth for all EMS settings from the database.
    ///
    /// On startup:
    ///   1. Seeds the Settings table from appsettings.json if it is empty.
    ///   2. Loads the record into Current.
    ///   3. Signals readiness via WaitForReadyAsync().
    ///
    /// After the user saves changes via the UI, call RefreshAsync().
    /// This reloads Current and fires SettingsChanged so all subscribers
    /// receive the updated Settings object immediately.
    /// </summary>
    public class SettingsService : IHostedService
    {
        private readonly SettingsDataService _settingsDataService;
        private readonly LoggingService<SettingsService> _logger;
        private readonly SettingsConfig _appsettings;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile Settings _current = new Settings();

        /// <summary>The current settings loaded from the database.</summary>
        public virtual Settings Current => _current;

        /// <summary>
        /// Fires after RefreshAsync() completes.
        /// The new Settings object is passed as argument so subscribers
        /// do not need to read Current themselves.
        /// </summary>
        public event Action<Settings, bool>? SettingsChanged; // bool = isStartup

        public SettingsService(
            SettingsDataService settingsDataService,
            LoggingService<SettingsService> logger,
            IOptions<SettingsConfig> appsettings)
        {
            _settingsDataService = settingsDataService;
            _logger = logger;
            _appsettings = appsettings.Value;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await EnsureDefaultsSeededAsync().ConfigureAwait(false);
            await PatchExistingRecordAsync().ConfigureAwait(false);
            await LoadAsync().ConfigureAwait(false);
            SettingsChanged?.Invoke(_current, true);
            _ready.TrySetResult();
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Awaitable: resolves once the initial load from the database is complete.
        /// Background services should await this before their first cycle.
        /// </summary>
        public Task WaitForReadyAsync() => _ready.Task;

        /// <summary>
        /// Reloads settings from the database and fires SettingsChanged.
        /// Call this after the user saves changes in the Settings UI.
        /// </summary>
        public async Task RefreshAsync()
        {
            await LoadAsync().ConfigureAwait(false);
            SettingsChanged?.Invoke(_current, false);
            _logger.LogInformation("SettingsService: settings refreshed — SettingsChanged fired.");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task PatchExistingRecordAsync()
        {
            var list = await _settingsDataService.GetList(set =>
                Task.FromResult(set.ToList())).ConfigureAwait(false);

            var record = list.FirstOrDefault();
            if (record == null) return;

            bool dirty = false;

            // Patch fields that were added by migration and default to 0/null
            // on existing rows because SQLite ADD COLUMN doesn't reliably
            // apply defaultValue to existing data.
            if (record.Latitude == 0.0 && record.Longitude == 0.0)
            {
                record.Latitude = 52.1;
                record.Longitude = 5.1;
                dirty = true;
            }

            if (record.CycleCost == 0.0)
            {
                record.CycleCost = 0.05;
                dirty = true;
            }

            if (record.SelfUseLookAheadQuarters == 0)
            {
                record.SelfUseLookAheadQuarters = 96;
                dirty = true;
            }

            if (record.ReserveSafetyFactor == 0.0)
            {
                record.ReserveSafetyFactor = 1.10;
                dirty = true;
            }

            if (record.SolarHeadroomSafetyFactor == 0.0)
            {
                record.SolarHeadroomSafetyFactor = 1.05;
                dirty = true;
            }

            if (record.CheapRefillToleranceEur == 0.0)
            {
                record.CheapRefillToleranceEur = 0.01;
                dirty = true;
            }

            if (record.ExportPremiumEur == 0.0)
            {
                record.ExportPremiumEur = 0.02;
                dirty = true;
            }

            if (record.NetZeroHomeMinProfit == 0.0)
            {
                record.NetZeroHomeMinProfit = 0.005;
                dirty = true;
            }

            if (string.IsNullOrWhiteSpace(record.TimeZone))
            {
                record.TimeZone = _appsettings.Timezone ?? "Europe/Amsterdam";
                dirty = true;
            }

            var energy = record.RequiredHomeEnergyArray;
            if (energy == null || energy.Length < 12)
            {
                record.RequiredHomeEnergyArray =
                [
                    19000, 15000, 14000, 12500, 12000, 10000,
                     9000, 10000, 12000, 13500, 16500, 19500
                ];
                dirty = true;
            }

            if (string.IsNullOrWhiteSpace(record.ExportDirectory))
            {
                record.ExportDirectory = "/data/exports";
                dirty = true;
            }

            if (!dirty) return;

            await _settingsDataService.Update(
                [record],
                (item, set) => set.FirstOrDefault(s => s.Id == item.Id)).ConfigureAwait(false);

            _logger.LogInformation("SettingsService: patched existing settings record with missing defaults.");
        }

        private async Task EnsureDefaultsSeededAsync()
        {
            var existing = await _settingsDataService.GetList(set =>
                Task.FromResult(set.ToList())).ConfigureAwait(false);

            if (existing.Count > 0)
                return;

            _logger.LogInformation("SettingsService: empty Settings table — seeding from appsettings.json.");

            var defaults = new Settings
            {
                // Timezone comes from appsettings; all other values are read
                // from the ManagementSettings section via the old SettingsConfig.
                // Since SettingsConfig now only carries Timezone and DatabaseBackupDirectory,
                // the remaining fields fall back to sensible neutral values.
                // The user should configure them via the Management Settings UI after first boot.
                TimeZone = _appsettings.Timezone ?? "Europe/Amsterdam",
                Latitude = 52.1,
                Longitude = 5.1,
                ChargedInControl = false,
                ManualOverride = false,
                CycleCost = 0.05,
                NetZeroHomeMinProfit = 0.005,
                SolarAnnualProductionKWh = 0.0,
                SolarSystemShutsDownDuringNegativePrices = false,
                StatisticsFromDate = null,
                ExportDirectory = "/data/exports",
            };

            defaults.ManualChargingHoursArray = [];
            defaults.ManualDischargingHoursArray = [];
            defaults.ManualNetZeroHomeHoursArray = [];
            defaults.RequiredHomeEnergyArray =
            [
                19000, 15000, 14000, 12500, 12000, 10000,
                 9000, 10000, 12000, 13500, 16500, 19500
            ];

            await _settingsDataService.Add(
                [defaults],
                (item, set) => set.Any()).ConfigureAwait(false);

            _logger.LogInformation("SettingsService: defaults seeded.");
        }

        private async Task LoadAsync()
        {
            try
            {
                var list = await _settingsDataService.GetList(set =>
                    Task.FromResult(set.ToList())).ConfigureAwait(false);

                var record = list.FirstOrDefault();
                if (record == null)
                {
                    _logger.LogWarning("SettingsService: no record in Settings table — using empty defaults.");
                    return;
                }

                _current = record;
                _logger.LogInformation("SettingsService: settings loaded from database.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"SettingsService: failed to load settings — using previous values. {ex.Message}");
            }
        }
    }
}