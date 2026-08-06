using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using System.Diagnostics;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// The database layer as the UI feels it. Runs against a real SQLite file with the real
    /// migrations, because every property under test here — WAL, concurrent reads, delete
    /// semantics — belongs to the provider, not to the C# around it.
    ///
    /// The read path used to be a synchronous SemaphoreSlim.Wait() that also handed back the
    /// caller's Task after disposing the context. That blocked a thread-pool thread per query and
    /// only worked because SQLite completes most calls synchronously.
    /// </summary>
    public class DbHelperConcurrencyTests : IDisposable
    {
        // Relative and starting with a dot: ModelContext runs the connection string through
        // DockerService.ConnectionString, which prefixes anything else with "." outside Docker.
        private readonly string _databasePath = $"./sessy_dbhelper_{Guid.NewGuid():N}.db";

        private readonly ServiceProvider _provider;

        public DbHelperConcurrencyTests()
        {
            var services = new ServiceCollection();

            services.AddDbContext<ModelContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            services.AddScoped<DbHelper>();

            _provider = services.BuildServiceProvider();

            using var scope = _provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ModelContext>();

            // Same order as Program.cs: WAL first, then the migrations.
            SqliteSetup.EnableWriteAheadLogging(context.Database.GetDbConnection());

            context.Database.Migrate();
        }

        private DbHelper NewHelper() =>
            _provider.CreateScope().ServiceProvider.GetRequiredService<DbHelper>();

        private AppVersionDataService NewService() =>
            new(_provider.GetRequiredService<IServiceScopeFactory>());

        [Fact]
        public async Task Query_context_stays_alive_until_the_delegate_finishes()
        {
            var helper = NewHelper();

            // The await inside forces a real continuation. If the scope were disposed on the way
            // out, as the old synchronous overload did, this second use would throw.
            var count = await helper.ExecuteQueryAsync(async db =>
            {
                await Task.Delay(50).ConfigureAwait(false);

                return await db.AppVersions.CountAsync();
            });

            Assert.Equal(0, count);
        }

        [Fact]
        public async Task Reads_run_concurrently()
        {
            var helper = NewHelper();

            async Task<int> SlowRead()
            {
                return await helper.ExecuteQueryAsync(async db =>
                {
                    var n = await db.AppVersions.CountAsync();
                    await Task.Delay(200).ConfigureAwait(false);

                    return n;
                });
            }

            var stopwatch = Stopwatch.StartNew();
            await Task.WhenAll(SlowRead(), SlowRead(), SlowRead());
            stopwatch.Stop();

            // Serialized this would be at least 600 ms; concurrent it is a little over 200.
            Assert.True(stopwatch.ElapsedMilliseconds < 500,
                $"three 200 ms reads took {stopwatch.ElapsedMilliseconds} ms — they are being serialized");
        }

        [Fact]
        public async Task RemoveWhere_deletes_and_commits()
        {
            var service = NewService();
            var now = new DateTime(2026, 8, 6, 14, 0, 0);

            await service.RecordStartupAsync("v1.0.1", "m1", now);
            await service.RecordStartupAsync("v1.0.2", "m1", now.AddDays(1));

            // ExecuteDelete runs outside the change tracker. Inside ExecuteTransaction — which only
            // commits when the tracker has changes — the delete would be rolled back on dispose.
            await service.RemoveWhere(v => v.Version == "v1.0.1");

            var left = await service.GetList(async set => await Task.FromResult(set.ToList()));

            Assert.Equal("v1.0.2", Assert.Single(left).Version);
        }

        [Fact]
        public async Task MatchOn_updates_existing_rows_and_adds_new_ones()
        {
            var service = NewService();
            var first = new DateTime(2026, 8, 6, 14, 0, 0);

            await service.RecordStartupAsync("v1.0.1", "m1", first);

            var batch = new List<AppVersion>
            {
                new() { Version = "v1.0.1", FirstSeen = first, LastSeen = first.AddHours(2), LastMigration = "m2" },
                new() { Version = "v1.0.2", FirstSeen = first, LastSeen = first.AddHours(3), LastMigration = "m2" },
            };

            await service.AddOrUpdate(batch, AppVersionDataService.MatchOn(v => v.Version));

            var rows = await service.GetList(async set => await Task.FromResult(set.OrderBy(v => v.Version).ToList()));

            Assert.Equal(2, rows.Count);
            Assert.Equal(first.AddHours(2), rows[0].LastSeen);   // existing row updated
            Assert.Equal("m2", rows[1].LastMigration);           // new row added
        }

        [Fact]
        public async Task Database_runs_in_wal_mode()
        {
            var helper = NewHelper();

            var mode = await helper.ExecuteQueryAsync(async db =>
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();

                command.CommandText = "PRAGMA journal_mode;";

                if (command.Connection!.State != System.Data.ConnectionState.Open)
                    await command.Connection.OpenAsync();

                return (string?)await command.ExecuteScalarAsync();
            });

            Assert.Equal("wal", mode?.ToLowerInvariant());
        }

        public void Dispose()
        {
            _provider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            foreach (var file in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
