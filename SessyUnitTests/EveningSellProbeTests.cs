using Microsoft.Data.Sqlite;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic for 06-10 17:15: why 18:45, 19:15 and 19:30 are not sold to the grid. Replays
    /// the stored plan inputs through the greedy planner with its why-not-sold trace. Skips itself
    /// when the local database is not there.
    /// </summary>
    public class EveningSellProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";
        private static readonly DateTime HorizonStart = new(2026, 10, 6, 17, 15, 0);
        private const double CapacityKWh = 16.2;

        private readonly ITestOutputHelper _output;
        public EveningSellProbeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Probe_evening_sell()
        {
            if (!File.Exists(DatabasePath)) return;

            var rows = new List<(DateTime Time, double Buy, double Sell, double NetLoadWh, double MinSocWh, double SocWh, double DisW)>();
            using (var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"select Time, BuyingPriceEurKWh, SellingPriceEurKWh, NetLoadWh, MinSocWh, PlannedChargeLeftWh, PlannedDischargePowerW
                      from PlannedQuarters where Time >= $start order by Time";
                command.Parameters.AddWithValue("$start", HorizonStart.ToString("yyyy-MM-dd HH:mm:ss"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((DateTime.Parse(reader.GetString(0)), reader.GetDouble(1), reader.GetDouble(2),
                              reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)));
            }
            if (rows.Count == 0) return;

            var points = rows.Select(r => new PricePoint(r.Time, r.Buy, r.Sell, r.NetLoadWh, Math.Max(0.0, -r.NetLoadWh))).ToList();
            var bounds = rows.Select(r => new SocBound(r.Time, r.MinSocWh / 1000.0, CapacityKWh)).ToList();

            // SOC at the start of 17:15: its end SOC plus what it drains.
            double initialSoc = (rows[0].SocWh + rows[0].DisW * 0.25 / 0.9) / 1000.0;

            var curve = new EfficiencyCurve(ChargeCeiling: 0.8796, ChargeOverheadKW: 0.070,
                                            DischargeCeiling: 0.915, DischargeOverheadKW: 0.071, Samples: 100);
            var spec = new BatterySpec(CapacityKWh, initialSoc, 6.6, 5.1, 0.87, 0.90, Efficiency: curve,
                DischargeCapability: new DischargeCapability(3676, 0.20, 20));
            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true,
                FutureValueDiscountPerHour: 0.003, ReservationPriceEurPerKWh: 0.20, AllowCarryForward: true);

            var trace = new List<string>();
            var result = BatteryGreedyPlanner.Solve(points, spec, opt, bounds, trace.Add)!;
            _output.WriteLine($"objective {result.ObjectiveEur:F3} EUR");

            // With vs without Candidate F, money per period.
            foreach (double cycle in new[] { 0.001, 0.05 })
            {
                var withShift = BatteryGreedyPlanner.Solve(points, spec, opt with { CycleCostEurPerKWh = cycle }, bounds)!;
                var noShift = BatteryGreedyPlanner.Solve(points, spec, opt with { CycleCostEurPerKWh = cycle, AllowShift = false }, bounds)!;
                _output.WriteLine("");
                _output.WriteLine($"cycle cost {cycle:F3}: objective with shift {withShift.ObjectiveEur:F3}, without {noShift.ObjectiveEur:F3}, " +
                                  $"gain {withShift.ObjectiveEur - noShift.ObjectiveEur:F3} EUR");
                Money("with shift   ", withShift, points);
                Money("without shift", noShift, points);
            }
        }

        private void Money(string label, PlanResult plan, List<PricePoint> points)
        {
            var periods = new[]
            {
                ("vandaag 17-24", HorizonStart, HorizonStart.Date.AddDays(1)),
                ("nacht 00-07  ", HorizonStart.Date.AddDays(1), HorizonStart.Date.AddDays(1).AddHours(7)),
                ("morgen 07-17 ", HorizonStart.Date.AddDays(1).AddHours(7), HorizonStart.Date.AddDays(1).AddHours(17)),
                ("morgen 17-24 ", HorizonStart.Date.AddDays(1).AddHours(17), HorizonStart.Date.AddDays(2)),
            };

            foreach (var (name, from, to) in periods)
            {
                double import = 0, importEur = 0, export = 0, exportEur = 0, discharged = 0;
                for (int i = 0; i < plan.Plan.Count; i++)
                {
                    var s = plan.Plan[i];
                    if (s.Start < from || s.Start >= to) continue;
                    var p = points[i];
                    double deficit = Math.Max(0, p.NetLoadWh / 1000.0), surplus = Math.Max(0, -p.NetLoadWh / 1000.0);
                    double ch = s.ChargeKW * 0.25, dis = s.DischargeKW * 0.25;
                    double net = deficit - surplus + ch - dis;   // grid: + import, - export
                    if (net > 0) { import += net; importEur += net * p.BuyEurPerKWh; }
                    else { export += -net; exportEur += -net * p.SellEurPerKWh; }
                    discharged += dis;
                }
                _output.WriteLine($"  {label} {name}: import {import,5:F2} kWh €{importEur,5:F2}, export {export,5:F2} kWh €{exportEur,5:F2}, " +
                                  $"net €{exportEur - importEur,6:F2}, discharged {discharged,5:F2} kWh");
            }
        }
    }
}
