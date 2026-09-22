using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyController.Services;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using SessyCommon.Services;
using Xunit;

namespace SessyTests.Services
{
    // TIJDELIJK — alleen voor analyse van de meegeleverde Sessy.db-kopie. Mag weer weg na gebruik.
    [Collection("Database")]
    public class YesterdayAnalysisTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string? _previousDocker;

        public YesterdayAnalysisTests(ITestOutputHelper output)
        {
            _output = output;

            // These analysis tests use an absolute Windows DB path, which DockerService only leaves
            // intact when it thinks it runs in Docker. Set the flag here and restore it in Dispose,
            // so this process-wide setting never leaks into other tests (which rely on the
            // non-Docker behaviour, where a leaked "true" turns "./x.db" into "/x.db" and fails).
            _previousDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER");
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER", "true");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER", _previousDocker);
        }

        // Kopie staat hier klaargezet, read-only openen — geen migraties, geen writes.
        private const string DbPath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        [Fact]
        public async Task Analyseer_gisteren()
        {
            var services = new ServiceCollection();

            services.AddDbContext<ModelContext>(options =>
                options.UseSqlite($"Data Source={DbPath}"));

            services.AddScoped<DbHelper>();
            services.AddScoped<EPEXPricesDataService>();
            services.AddScoped<QuarterlyMeasurementDataService>();
            services.AddScoped<TaxesDataService>();
            services.AddScoped<EnergyHistoryDataService>();
            services.AddScoped<InverterMeasurementDataService>();
            services.AddScoped<ConsumptionDataService>();
            services.AddScoped<PlannedQuarterDataService>();
            services.AddScoped<ActualQuarterDataService>();
            services.AddSingleton<TimeZoneService>();
            services.AddScoped<ICalculationService, CalculationService>();
            services.AddScoped<QuarterlyFactsService>();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            // Gisteren = dag vóór vandaag in de DB-eigen tijdzone (Europe/Amsterdam).
            var tz = sp.GetRequiredService<TimeZoneService>();
            var today = tz.Now.Date;
            var start = today.AddDays(-1);
            var end = start.AddHours(23).AddMinutes(45);

            _output.WriteLine($"Analyse periode: {start:yyyy-MM-dd HH:mm} .. {end:yyyy-MM-dd HH:mm}");

            var facts = sp.GetRequiredService<QuarterlyFactsService>();
            var measured = await facts.GetAsync(start, end);

            var plannedService = sp.GetRequiredService<PlannedQuarterDataService>();
            var planned = await plannedService.GetList(async set =>
                await Task.FromResult(set.Where(p => p.Time >= start && p.Time <= end)
                                          .OrderBy(p => p.Time).ToList()));

            var actualService = sp.GetRequiredService<ActualQuarterDataService>();
            var actual = await actualService.GetList(async set =>
                await Task.FromResult(set.Where(a => a.Time >= start && a.Time <= end)
                                          .OrderBy(a => a.Time).ToList()));

            _output.WriteLine($"Measured quarters: {measured.Count}, Planned: {planned.Count}, Actual: {actual.Count}");
            _output.WriteLine("");
            _output.WriteLine("Tijd  | Mode(plan/act) | Batt.W(meas) | GridImportWh | GridExportWh | Buy | Sell | SolarkWh | ConsW | PlanRevEur");

            double totalImportCost = 0.0;
            double totalExportRevenue = 0.0;
            double totalPlannedRevenue = 0.0;

            foreach (var m in measured)
            {
                var p = planned.FirstOrDefault(x => x.Time == m.Time);
                var a = actual.FirstOrDefault(x => x.Time == m.Time);

                double importCost = m.GridImportWh / 1000.0 * m.BuyingPriceEur;
                double exportRevenue = m.GridExportWh / 1000.0 * m.SellingPriceEur;

                totalImportCost += importCost;
                totalExportRevenue += exportRevenue;
                totalPlannedRevenue += m.PlannedRevenueEur;

                _output.WriteLine(
                    $"{m.Time:HH:mm} | {p?.PlannedMode,-11}/{a?.ActualMode,-11} | {m.BatteryPowerWatts,7:F0} | " +
                    $"{m.GridImportWh,7:F0} | {m.GridExportWh,7:F0} | {m.BuyingPriceEur,6:F4} | {m.SellingPriceEur,6:F4} | " +
                    $"{m.SolarProductionKWh,6:F2} | {m.ConsumptionAverageW,6:F0} | {m.PlannedRevenueEur,7:F4}" +
                    (a != null && a.StateMachineReason.Length > 0 ? $"  [{a.StateMachineReason}]" : ""));
            }

            _output.WriteLine("");
            _output.WriteLine($"Totaal grid-inkoopkosten: {totalImportCost:F2} EUR");
            _output.WriteLine($"Totaal grid-verkoopopbrengst: {totalExportRevenue:F2} EUR");
            _output.WriteLine($"Netto elektriciteitsrekening (kosten - opbrengst): {(totalImportCost - totalExportRevenue):F2} EUR");
            _output.WriteLine($"Som geplande revenue (MilpService): {totalPlannedRevenue:F2} EUR");
        }

        [Fact]
        public async Task Analyseer_legionella_venster()
        {
            var services = new ServiceCollection();
            services.AddDbContext<ModelContext>(options => options.UseSqlite($"Data Source={DbPath}"));
            services.AddScoped<DbHelper>();
            services.AddScoped<EPEXPricesDataService>();
            services.AddScoped<QuarterlyMeasurementDataService>();
            services.AddScoped<TaxesDataService>();
            services.AddScoped<EnergyHistoryDataService>();
            services.AddScoped<InverterMeasurementDataService>();
            services.AddScoped<ConsumptionDataService>();
            services.AddSingleton<TimeZoneService>();
            services.AddScoped<ICalculationService, CalculationService>();
            services.AddScoped<QuarterlyFactsService>();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            var tz = sp.GetRequiredService<TimeZoneService>();
            var today = tz.Now.Date;
            var day = today.AddDays(-1);

            // Ruim venster rond het gemelde Legionella-programma.
            var start = day.AddHours(11);
            var end = day.AddHours(15);

            var consumptionService = sp.GetRequiredService<ConsumptionDataService>();
            var rows = await consumptionService.GetList(async set =>
                await Task.FromResult(set.Where(c => c.Time >= start && c.Time <= end)
                                          .OrderBy(c => c.Time).ToList()));

            var energyHistoryService = sp.GetRequiredService<EnergyHistoryDataService>();
            var histories = await energyHistoryService.GetList(async set =>
                await Task.FromResult(set.Where(h => h.Time >= start.AddMinutes(-15) && h.Time <= end)
                                          .OrderBy(h => h.Time).ToList()));
            var gridByTime = QuarterlyFactsService.GridDeltas(histories);

            var calc = sp.GetRequiredService<ICalculationService>();
            var prices = await calc.CalculateEnergyPricesBatchAsync(rows.Select(r => r.Time));

            _output.WriteLine("Tijd  | ConsumptieW | GridImportWh | GridExportWh | BuyPrice | KostenEur");

            double totalKWh = 0.0;
            double totalCost = 0.0;

            foreach (var r in rows)
            {
                gridByTime.TryGetValue(r.Time, out var grid);
                prices.TryGetValue(r.Time, out var price);

                double kwh = r.ConsumptionWh * 0.25 / 1000.0;
                double cost = grid.importWh / 1000.0 * (price?.Buying ?? 0.0);

                totalKWh += kwh;
                totalCost += cost;

                _output.WriteLine(
                    $"{r.Time:HH:mm} | {r.ConsumptionWh,10:F0} | {grid.importWh,11:F0} | {grid.exportWh,11:F0} | " +
                    $"{price?.Buying ?? 0.0,8:F4} | {cost,9:F4}");
            }

            _output.WriteLine("");
            _output.WriteLine($"Totaal verbruik 11:00-15:00: {totalKWh:F2} kWh");
            _output.WriteLine($"Totaal netkosten (import x prijs) 11:00-15:00: {totalCost:F2} EUR");
        }
    }
}
