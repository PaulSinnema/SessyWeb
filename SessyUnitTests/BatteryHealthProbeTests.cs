using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyController.Services.Statistics;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>Prints the measured capacity per battery and month on the local production copy.</summary>
    [Trait("Category", "Probe")]
    [Collection("Database")]
    public class BatteryHealthProbeTests : IDisposable
    {
        private const string DbPath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        private readonly ITestOutputHelper _output;
        private readonly string? _previousDocker;

        public BatteryHealthProbeTests(ITestOutputHelper output)
        {
            _output = output;
            // DockerService only keeps an absolute Windows path intact when it thinks it runs in Docker.
            _previousDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER");
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER", "true");
        }

        public void Dispose() => Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER", _previousDocker);

        [Fact]
        public async Task Probe_battery_health()
        {
            if (!File.Exists(DbPath)) return;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ModelContext>(options => options.UseSqlite($"Data Source={DbPath}"));
            services.AddScoped<DbHelper>();
            services.AddScoped<BatteryQuarterMeasurementDataService>();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var rows = await scope.ServiceProvider.GetRequiredService<BatteryQuarterMeasurementDataService>()
                .GetList(s => Task.FromResult(s.ToList()));

            foreach (var battery in rows.GroupBy(r => r.BatteryId))
            {
                var list = battery.ToList();
                var runs = BatteryHealth.Runs(list);
                _output.WriteLine($"{battery.Key}: {list.Count} rows {list.Min(r => r.Time):d}..{list.Max(r => r.Time):d}, {runs.Count} runs");

                var health = BatteryHealth.Estimate(list, DateTime.Today);
                if (health == null) continue;

                _output.WriteLine($"  nominal {health.NominalKWh:F2} kWh, baseline {health.BaselineKWh:F2}, current {health.CurrentPercent:F1} %, fade {health.FadePercentPerYear:F2} %/yr, 70 % at {health.EndOfLife:d}");
                foreach (var m in health.Months)
                    _output.WriteLine($"  {m.Label}: {m.CapacityKWh:F2} kWh ({m.Percent:F1} %) from {m.Runs} runs");
            }
        }
    }
}
