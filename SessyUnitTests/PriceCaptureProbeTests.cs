using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Statistics;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>Times Price capture on the local production copy and prints its months.</summary>
    [Trait("Category", "Probe")]
    [Collection("Database")]
    public class PriceCaptureProbeTests : IDisposable
    {
        private const string DbPath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        private readonly ITestOutputHelper _output;
        private readonly string? _previousDocker;

        public PriceCaptureProbeTests(ITestOutputHelper output)
        {
            _output = output;
            // DockerService only keeps an absolute Windows path intact when it thinks it runs in Docker.
            _previousDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER");
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER", "true");
        }

        public void Dispose() => Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER", _previousDocker);

        [Fact]
        public async Task Probe_price_capture()
        {
            if (!File.Exists(DbPath)) return;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ModelContext>(options => options.UseSqlite($"Data Source={DbPath}"));
            services.AddScoped<DbHelper>();
            services.AddScoped<EPEXPricesDataService>();
            services.AddScoped<QuarterlyMeasurementDataService>();
            services.AddScoped<TaxesDataService>();
            services.AddScoped<EnergyHistoryDataService>();
            services.AddScoped<InverterMeasurementDataService>();
            services.AddScoped<ConsumptionDataService>();
            services.AddScoped<SettingsDataService>();
            services.AddScoped<InvestmentDataService>();
            services.AddScoped<InvestmentGroupDataService>();
            services.AddSingleton(typeof(LoggingService<>));
            services.AddScoped<SettingsService>();
            services.AddSingleton<TimeZoneService>();
            services.AddScoped<ICalculationService, CalculationService>();
            services.AddScoped<QuarterlyFactsService>();
            services.AddScoped<PriceCaptureService>();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            await sp.GetRequiredService<SettingsService>().RefreshAsync();

            var to = sp.GetRequiredService<TimeZoneService>().Now.Date;
            var from = to.AddYears(-1);

            var watch = Stopwatch.StartNew();

            async Task Time(string name, Func<Task<int>> work)
            {
                var w = Stopwatch.StartNew();
                var count = await work();
                _output.WriteLine($"{name}: {count} rows in {w.ElapsedMilliseconds} ms");
            }

            await Time("QuarterlyMeasurements year", async () => (await sp.GetRequiredService<QuarterlyMeasurementDataService>().GetList(s => Task.FromResult(s.Where(m => m.Time >= from && m.Time <= to).ToList()))).Count);
            await Time("QuarterlyMeasurements all reliable", async () => (await sp.GetRequiredService<QuarterlyMeasurementDataService>().GetList(s => Task.FromResult(s.Where(m => m.IsReliable).ToList()))).Count);
            await Time("EnergyHistory year", async () => (await sp.GetRequiredService<EnergyHistoryDataService>().GetList(s => Task.FromResult(s.Where(m => m.Time >= from && m.Time <= to).ToList()))).Count);
            await Time("EnergyHistory all", async () => (await sp.GetRequiredService<EnergyHistoryDataService>().GetList(s => Task.FromResult(s.ToList()))).Count);
            await Time("InverterMeasurements all", async () => (await sp.GetRequiredService<InverterMeasurementDataService>().GetList(s => Task.FromResult(s.ToList()))).Count);
            await Time("Consumption all", async () => (await sp.GetRequiredService<ConsumptionDataService>().GetList(s => Task.FromResult(s.ToList()))).Count);
            await Time("EPEX year", async () => (await sp.GetRequiredService<EPEXPricesDataService>().GetList(s => Task.FromResult(s.Where(m => m.Time >= from && m.Time <= to).ToList()))).Count);

            var times = Enumerable.Range(0, 365 * 96).Select(i => from.AddMinutes(15 * i)).ToList();
            await Time("Price batch year (cold)", async () => (await sp.GetRequiredService<ICalculationService>().CalculateEnergyPricesBatchAsync(times)).Count);
            await Time("Price batch year (warm)", async () => (await sp.GetRequiredService<ICalculationService>().CalculateEnergyPricesBatchAsync(times)).Count);

            watch.Restart();
            var quarters = await sp.GetRequiredService<QuarterlyFactsService>().GetAsync(from, to);
            _output.WriteLine($"QuarterlyFacts: {quarters.Count} quarters in {watch.ElapsedMilliseconds} ms");

            watch.Restart();
            var months = await sp.GetRequiredService<PriceCaptureService>().GetAsync(from, to);
            _output.WriteLine($"PriceCapture: {months.Count} months in {watch.ElapsedMilliseconds} ms");

            foreach (var m in months)
                _output.WriteLine($"{m.Label}: captured {m.CapturedEurPerKWh:0.0000} best {m.BestEurPerKWh:0.0000} pct {m.CapturePercent:0.0} kWh {m.DischargedKWh:0.0} days {m.Days}");
        }
    }
}
