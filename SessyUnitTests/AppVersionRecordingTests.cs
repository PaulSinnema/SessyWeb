using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// The version stamp Program.cs writes right after Database.Migrate(). Runs against a real
    /// SQLite file with the real migrations applied, because the point of the feature is exactly
    /// what ends up in the file.
    /// </summary>
    public class AppVersionRecordingTests : IDisposable
    {
        // Relative and starting with a dot: ModelContext runs the connection string through
        // DockerService.ConnectionString, which prefixes anything else with "." outside Docker.
        private readonly string _databasePath = $"./sessy_appversion_{Guid.NewGuid():N}.db";

        private readonly ServiceProvider _provider;

        public AppVersionRecordingTests()
        {
            var services = new ServiceCollection();

            services.AddDbContext<ModelContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            services.AddScoped<DbHelper>();

            _provider = services.BuildServiceProvider();

            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<ModelContext>().Database.Migrate();
        }

        private AppVersionDataService NewService() =>
            new(_provider.GetRequiredService<IServiceScopeFactory>());

        [Fact]
        public async Task First_startup_records_the_version()
        {
            var service = NewService();
            var now = new DateTime(2026, 8, 6, 14, 0, 0);

            var previous = await service.RecordStartupAsync("v1.0.39", "20260806121431_AddAppVersion", now);

            Assert.Null(previous);   // nothing ran against this database before

            var rows = await service.GetList(async set => await Task.FromResult(set.ToList()));

            var row = Assert.Single(rows);
            Assert.Equal("v1.0.39", row.Version);
            Assert.Equal(now, row.FirstSeen);
            Assert.Equal(now, row.LastSeen);
            Assert.Equal("20260806121431_AddAppVersion", row.LastMigration);
        }

        [Fact]
        public async Task Restarting_the_same_version_moves_last_seen_but_keeps_first_seen()
        {
            var service = NewService();
            var first = new DateTime(2026, 8, 6, 14, 0, 0);
            var second = first.AddDays(3);

            await service.RecordStartupAsync("v1.0.39", "20260806121431_AddAppVersion", first);
            await service.RecordStartupAsync("v1.0.39", "20260806121431_AddAppVersion", second);

            var rows = await service.GetList(async set => await Task.FromResult(set.ToList()));

            var row = Assert.Single(rows);
            Assert.Equal(first, row.FirstSeen);    // [SkipCopy] keeps this one
            Assert.Equal(second, row.LastSeen);
        }

        [Fact]
        public async Task A_new_version_is_added_and_the_previous_one_is_reported()
        {
            var service = NewService();
            var first = new DateTime(2026, 8, 6, 14, 0, 0);

            await service.RecordStartupAsync("v1.0.38", "20260804211046_AddProjectedCostBasisToPlannedQuarter", first);
            var previous = await service.RecordStartupAsync("v1.0.39", "20260806121431_AddAppVersion", first.AddDays(1));

            Assert.NotNull(previous);
            Assert.Equal("v1.0.38", previous!.Version);

            var rows = await service.GetList(async set => await Task.FromResult(set.OrderBy(v => v.LastSeen).ToList()));

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "v1.0.38", "v1.0.39" }, rows.Select(v => v.Version));
        }

        public void Dispose()
        {
            _provider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
    }
}
