using Microsoft.Data.Sqlite;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic: replays the latest stored plan inputs and checks per mode whether the runtime
    /// can execute what the plan says. Zero Net Home covers the whole house at runtime, so a ZNH
    /// quarter that plans only part of the deficit is a mismatch. Skips without the local database.
    /// </summary>
    public class PlanInvariantProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";
        private const double CapacityKWh = 16.2;

        private readonly ITestOutputHelper _output;
        public PlanInvariantProbeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Probe_plan_invariants()
        {
            if (!File.Exists(DatabasePath)) return;

            var starts = new[]
            {
                new DateTime(2026, 10, 6, 17, 15, 0),
                new DateTime(2026, 10, 7, 13, 30, 0),
            };

            foreach (var start in starts)
            {
                var rows = Load(start);
                if (rows.Count == 0) continue;

                var points = rows.Select(r => new PricePoint(r.Time, r.Buy, r.Sell, r.NetLoadWh, Math.Max(0.0, -r.NetLoadWh))).ToList();
                var bounds = rows.Select(r => new SocBound(r.Time, r.MinSocWh / 1000.0, CapacityKWh)).ToList();
                double initialSoc = (rows[0].SocWh + rows[0].DisW * 0.25 / 0.9) / 1000.0;

                var curve = new EfficiencyCurve(ChargeCeiling: 0.8796, ChargeOverheadKW: 0.070,
                                                DischargeCeiling: 0.915, DischargeOverheadKW: 0.071, Samples: 100);
                var spec = new BatterySpec(CapacityKWh, initialSoc, 6.6, 5.1, 0.87, 0.90, Efficiency: curve,
                    DischargeCapability: new DischargeCapability(3676, 0.20, 20));
                var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true,
                    FutureValueDiscountPerHour: 0.003, ReservationPriceEurPerKWh: 0.20, AllowCarryForward: true);

                foreach (bool shift in new[] { true, false })
                {
                    var plan = BatteryGreedyPlanner.Solve(points, spec, opt with { AllowShift = shift }, bounds)!;
                    _output.WriteLine($"── start {start:dd-MM HH:mm}, shift {shift}, soc0 {initialSoc:F2}, objective {plan.ObjectiveEur:F3}");

                    var counts = plan.Plan.GroupBy(p => p.Mode).Select(g => $"{g.Key}={g.Count()}");
                    _output.WriteLine("   modes: " + string.Join(", ", counts));

                    int partial = 0, belowMin = 0, idleWithDischarge = 0;
                    double partialKWh = 0.0;
                    for (int i = 0; i < plan.Plan.Count; i++)
                    {
                        var s = plan.Plan[i];
                        double deficit = Math.Max(0.0, points[i].NetLoadWh / 1000.0);
                        double dis = s.DischargeKW * 0.25;
                        double minSoc = bounds[i].MinSocKWh;

                        if (s.SocEndKWh < minSoc - 0.01)
                        {
                            belowMin++;
                            _output.WriteLine($"   BELOW MIN {s.Start:dd-MM HH:mm} {s.Mode} socEnd {s.SocEndKWh:F3} min {minSoc:F3}");
                        }

                        if ((s.Mode == ActionMode.SolarOnly || s.Mode == ActionMode.Disabled) && dis > 1e-6)
                        {
                            idleWithDischarge++;
                            _output.WriteLine($"   IDLE WITH DISCHARGE {s.Start:dd-MM HH:mm} {s.Mode} dis {dis:F3}");
                        }

                        // ZNH executes as full house cover; the plan only accounts for dis.
                        if (s.Mode == ActionMode.ZeroNetHome && deficit > 1e-6 && dis < deficit - 0.005)
                        {
                            partial++;
                            partialKWh += deficit - dis;
                            double minFrom = bounds.Skip(i).Max(b => b.MinSocKWh);
                            _output.WriteLine($"   ZNH PARTIAL {s.Start:dd-MM HH:mm} deficit {deficit:F3} planned {dis:F3} socStart {s.SocStartKWh:F3} socEnd {s.SocEndKWh:F3} min {minSoc:F3} minFrom {minFrom:F3}");
                        }
                    }

                    _output.WriteLine($"   ZNH partial quarters {partial} ({partialKWh:F2} kWh unplanned drain), below min {belowMin}, idle with discharge {idleWithDischarge}");
                }
            }
        }

        private static List<(DateTime Time, double Buy, double Sell, double NetLoadWh, double MinSocWh, double SocWh, double DisW)> Load(DateTime start)
        {
            var rows = new List<(DateTime, double, double, double, double, double, double)>();
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                @"select Time, BuyingPriceEurKWh, SellingPriceEurKWh, NetLoadWh, MinSocWh, PlannedChargeLeftWh, PlannedDischargePowerW
                  from PlannedQuarters where Time >= $start and Time < $end order by Time";
            command.Parameters.AddWithValue("$start", start.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$end", start.AddHours(30).ToString("yyyy-MM-dd HH:mm:ss"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((DateTime.Parse(reader.GetString(0)), reader.GetDouble(1), reader.GetDouble(2),
                          reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)));
            return rows;
        }
    }
}
