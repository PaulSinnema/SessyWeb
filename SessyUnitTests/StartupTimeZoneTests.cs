using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Services;
using SessyData.Helpers;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Where the timezone comes from at startup.
    ///
    /// It used to sit in appsettings.json as well, and the two could disagree: the pre-migration
    /// backup and the AppVersions stamp are written before the hosted services load the Settings
    /// row, so they ran on the configured zone while everything after them ran on the stored one.
    /// The Settings row is now the only source, which means startup has to read it itself — before
    /// Migrate, so against a schema that may be older than the model or missing altogether.
    ///
    /// Runs against a real SQLite file with the real migrations: the point under test is what the
    /// provider does with a table that is not there yet.
    /// </summary>
    public class StartupTimeZoneTests : IDisposable
    {
        // Relative and starting with a dot, like DbHelperConcurrencyTests: ModelContext runs the
        // connection string through DockerService.ConnectionString.
        private readonly string _databasePath = $"./sessy_timezone_{Guid.NewGuid():N}.db";
        private readonly ServiceProvider _provider;

        public StartupTimeZoneTests()
        {
            var services = new ServiceCollection();

            services.AddDbContext<ModelContext>(options => options.UseSqlite($"Data Source={_databasePath}"));

            _provider = services.BuildServiceProvider();
        }

        private ModelContext NewContext() =>
            _provider.CreateScope().ServiceProvider.GetRequiredService<ModelContext>();

        [Fact]
        public void A_database_without_a_settings_table_reads_as_no_timezone()
        {
            using var context = NewContext();

            // Opens the file without migrating: this is the first-run path, where startup must fall
            // back to the default instead of throwing before the application is even up.
            SqliteSetup.EnableWriteAheadLogging(context.Database.GetDbConnection());

            Assert.Null(SqliteSetup.TryReadTimeZone(context.Database.GetDbConnection()));
        }

        [Fact]
        public void A_migrated_database_already_carries_the_seeded_zone()
        {
            using var context = NewContext();

            context.Database.Migrate();

            // Migration AddSettingsTable inserts the Settings row itself, so from the first
            // migration onwards there is always a zone to read — SettingsService.EnsureDefaults-
            // SeededAsync only fires on a table that is genuinely empty.
            Assert.Equal(TimeZoneService.DefaultTimeZone,
                SqliteSetup.TryReadTimeZone(context.Database.GetDbConnection()));
        }

        [Fact]
        public void The_stored_timezone_is_read_back()
        {
            using var context = NewContext();

            context.Database.Migrate();

            var settings = context.Settings.First();
            settings.TimeZone = "Europe/Zurich";
            context.SaveChanges();

            Assert.Equal("Europe/Zurich", SqliteSetup.TryReadTimeZone(context.Database.GetDbConnection()));
        }

        [Fact]
        public void A_new_service_starts_on_the_default_zone()
        {
            // Deliberately no UpdateTimezone here: TimeZoneService keeps the active zone in a
            // static field, so changing it would leak into every other test in the assembly.
            var service = new TimeZoneService();

            Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById(TimeZoneService.DefaultTimeZone), service.TimeZone);
        }

        public void Dispose()
        {
            _provider.Dispose();

            SqliteConnectionCleanup();

            foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        /// <summary>SQLite keeps the file handle in its connection pool until it is cleared.</summary>
        private static void SqliteConnectionCleanup() =>
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
