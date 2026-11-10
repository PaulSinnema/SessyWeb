using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Charged's schedule history keeps only quarters that changed; runs against a real migrated
    /// SQLite file because what ends up in the table is the point.
    /// </summary>
    [Collection("Database")]
    public class ChargedScheduleHistoryTests : IDisposable
    {
        // Relative and starting with a dot: see AppVersionRecordingTests.
        private readonly string _databasePath = $"./sessy_chargedhistory_{Guid.NewGuid():N}.db";

        private static readonly DateTime Q0 = new(2026, 11, 11, 2, 0, 0);
        private static readonly DateTime Q1 = Q0.AddMinutes(15);
        private static readonly DateTime Q2 = Q0.AddMinutes(30);

        private readonly ServiceProvider _provider;

        public ChargedScheduleHistoryTests()
        {
            var services = new ServiceCollection();

            services.AddDbContext<ModelContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            services.AddScoped<DbHelper>();

            _provider = services.BuildServiceProvider();

            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<ModelContext>().Database.Migrate();
        }

        private ChargedScheduleQuarterDataService NewService() =>
            new(_provider.GetRequiredService<IServiceScopeFactory>());

        private static async Task<List<ChargedScheduleQuarter>> All(ChargedScheduleQuarterDataService service) =>
            await service.GetList(async set => await Task.FromResult(set.OrderBy(q => q.Time).ThenBy(q => q.FetchedAt).ToList()));

        [Fact]
        public async Task First_read_stores_every_quarter()
        {
            var service = NewService();

            int written = await service.SaveChangedQuartersAsync(
                new Dictionary<DateTime, double> { [Q0] = -2000, [Q1] = -1500 }, Q0.AddHours(-3));

            Assert.Equal(2, written);
            Assert.Equal(2, (await All(service)).Count);
        }

        [Fact]
        public async Task Unchanged_read_stores_nothing()
        {
            var service = NewService();
            var schedule = new Dictionary<DateTime, double> { [Q0] = -2000, [Q1] = -1500 };

            await service.SaveChangedQuartersAsync(schedule, Q0.AddHours(-3));
            int written = await service.SaveChangedQuartersAsync(schedule, Q0.AddHours(-2));

            Assert.Equal(0, written);
            Assert.Equal(2, (await All(service)).Count);
        }

        [Fact]
        public async Task Only_changed_and_new_quarters_are_added_as_new_versions()
        {
            var service = NewService();
            var first = Q0.AddHours(-3);
            var second = Q0.AddHours(-2);

            await service.SaveChangedQuartersAsync(new Dictionary<DateTime, double> { [Q0] = -2000, [Q1] = -1500 }, first);
            int written = await service.SaveChangedQuartersAsync(
                new Dictionary<DateTime, double> { [Q0] = -2000, [Q1] = -800, [Q2] = -500 }, second);

            Assert.Equal(2, written);   // Q1 changed, Q2 new, Q0 unchanged

            var rows = await All(service);
            Assert.Equal(4, rows.Count);
            Assert.Equal(new[] { -1500.0, -800.0 }, rows.Where(q => q.Time == Q1).Select(q => q.PowerW));
            Assert.Equal(second, rows.Single(q => q.Time == Q2).FetchedAt);
        }

        [Fact]
        public async Task Cleanup_removes_only_quarters_before_the_cutoff()
        {
            var service = NewService();

            await service.SaveChangedQuartersAsync(
                new Dictionary<DateTime, double> { [Q0.AddDays(-40)] = -2000, [Q0] = -1500 }, Q0.AddHours(-3));
            await service.RemoveOlderThanAsync(Q0.Date.AddDays(-30));

            var row = Assert.Single(await All(service));
            Assert.Equal(Q0, row.Time);
        }

        public void Dispose()
        {
            _provider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
    }
}
