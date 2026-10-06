using Microsoft.Data.Sqlite;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic harness for 06-10 13:07: why the plan charges 13:15-14:00 and 15:00 but not
    /// 14:15-14:45. Runs the real greedy planner on the stored inputs, taking one input away at a
    /// time. Skips itself when the local database is not there.
    /// </summary>
    public class AfternoonChargeProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";
        private static readonly DateTime HorizonStart = new(2026, 10, 6, 13, 0, 0);
        private const double CapacityKWh = 16.2;

        private readonly ITestOutputHelper _output;
        public AfternoonChargeProbeTests(ITestOutputHelper output) => _output = output;

        private sealed record Quarter(DateTime Time, string Mode, double PlanW, double LeftWh,
                                      double Buy, double Sell, double NetLoadWh, double MinSocWh);

        private static List<Quarter> ReadPlan()
        {
            var rows = new List<Quarter>();
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                @"select Time, PlannedMode, PlannedPowerW, PlannedChargeLeftWh,
                         BuyingPriceEurKWh, SellingPriceEurKWh, NetLoadWh, MinSocWh
                  from PlannedQuarters where Time >= $start order by Time";
            command.Parameters.AddWithValue("$start", HorizonStart.ToString("yyyy-MM-dd HH:mm:ss"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new Quarter(DateTime.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                    reader.GetDouble(6), reader.GetDouble(7)));
            return rows;
        }

        private static List<(double Soc, double PowerW)> Samples(int batteryMode)
        {
            var samples = new List<(double, double)>();
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                @"select BatteryStateOfChargeWh, abs(BatteryPowerWatts) from QuarterlyMeasurements
                  where BatteryMode = $mode and IsReliable = 1";
            command.Parameters.AddWithValue("$mode", batteryMode);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                double soc = reader.GetDouble(0) / (CapacityKWh * 1000.0);
                if (soc is >= 0.0 and <= 1.0) samples.Add((soc, reader.GetDouble(1)));
            }
            return samples;
        }

        [Fact]
        public void Probe_afternoon_charging()
        {
            if (!File.Exists(DatabasePath)) return;

            var quarters = ReadPlan();
            var points = quarters.Select(q => new PricePoint(q.Time, q.Buy, q.Sell, q.NetLoadWh,
                Math.Max(0.0, -q.NetLoadWh))).ToList();
            var bounds = quarters.Select(q => new SocBound(q.Time, q.MinSocWh / 1000.0, CapacityKWh)).ToList();

            // Efficiency read back from the plan itself (13:15-15:00 charging, 17:45-19:45 discharging).
            var curve = new EfficiencyCurve(ChargeCeiling: 0.8796, ChargeOverheadKW: 0.070,
                                            DischargeCeiling: 0.915, DischargeOverheadKW: 0.071, Samples: 100);
            var capability = ThrottleAnalysisService.FitDischargeCapability(Samples(2), 5100.0);
            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(Samples(1), 6600.0);
            _output.WriteLine($"discharge capability: plateau {capability.PlateauW:F0} W, knee {capability.KneeSoc:P0}, {capability.Samples} samples");
            _output.WriteLine($"charge floor at 40%: {floor.PowerW(0.4):F0} W");

            BatterySpec Spec(double initialSoc, DischargeCapability? cap) =>
                new(CapacityKWh, initialSoc, 6.6, 5.1, 0.87, 0.90,
                    ChargeTaper: null, Efficiency: curve, DischargeCapability: cap, ChargeFloor: floor);

            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true,
                FutureValueDiscountPerHour: 0.003, ReservationPriceEurPerKWh: 0.20, AllowCarryForward: true);

            Show("PRODUCTION", quarters.Select(q => (q.Time, q.Mode, q.PlanW, q.LeftWh)).ToList());

            Run("reproduction (SOC 0.467)", opt, Spec(0.467, capability), points, bounds);
            Run("no discharge capability", opt, Spec(0.467, null), points, bounds);
            Run("no discount", opt with { FutureValueDiscountPerHour = 0.0 }, Spec(0.467, capability), points, bounds);
            Run("no carry-forward", opt with { AllowCarryForward = false }, Spec(0.467, capability), points, bounds);
            Run("reserve 0", opt, Spec(0.467, capability), points,
                bounds.Select(b => b with { MinSocKWh = 0.0 }).ToList());
            Run("today only (no predicted tomorrow)", opt, Spec(0.467, capability),
                points.Where(p => p.Start.Date == HorizonStart.Date).ToList(),
                bounds.Where(b => b.Time.Date == HorizonStart.Date).ToList());

            // v1.0.146: sustained charge capability from SOC gain, same filter as the service.
            var chargeCapability = ThrottleAnalysisService.FitChargeCapability(SustainedChargeSamples());
            _output.WriteLine("");
            _output.WriteLine($"charge capability (DC W) per 10% bin: {string.Join(" ", chargeCapability.BinPowerW.Select(w => w.ToString("F0")))}");
            Run("+ measured charge capability", opt, Spec(0.467, capability) with { ChargeCapability = chargeCapability }, points, bounds);
        }

        private static List<(double Soc, double PowerW)> SustainedChargeSamples()
        {
            var samples = new List<(double, double)>();
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                @"select prev.BatteryStateOfChargeWh, m.BatteryStateOfChargeWh - prev.BatteryStateOfChargeWh
                  from QuarterlyMeasurements m
                  join QuarterlyMeasurements prev on prev.Time = datetime(m.Time, '-15 minutes')
                  join PlannedQuarters p on p.Time = m.Time
                  where m.Time >= $start and m.Time < $end
                    and m.BatteryMode = 1 and prev.BatteryMode = 1
                    and m.IsReliable = 1 and prev.IsReliable = 1
                    and p.PlannedUnthrottledPowerW >= 0.9 * 6600";
            command.Parameters.AddWithValue("$start", HorizonStart.AddDays(-60).ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$end", HorizonStart.ToString("yyyy-MM-dd HH:mm:ss"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                double gain = reader.GetDouble(1);
                if (gain > 0) samples.Add((reader.GetDouble(0) / (CapacityKWh * 1000.0), gain * 4.0));
            }
            return samples;
        }

        private void Run(string label, SessyOptions opt, BatterySpec spec, List<PricePoint> points, List<SocBound> bounds)
        {
            var result = BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;
            Show($"{label}  obj {result.ObjectiveEur:F3}",
                result.Plan.Select(p => (p.Start, p.Mode.ToString(),
                    p.ChargeKW > 0 ? p.ChargeKW * 1000 : -p.DischargeKW * 1000, p.SocEndKWh * 1000)).ToList());
        }

        private void Show(string label, List<(DateTime Time, string Mode, double PowerW, double SocWh)> rows)
        {
            _output.WriteLine("");
            _output.WriteLine(label);
            var today = rows.Where(r => r.Time >= HorizonStart && r.Time < HorizonStart.Date.AddHours(21)).ToList();
            _output.WriteLine("  " + string.Join(" ", today.Select(r => $"{r.Time:HHmm}:{(Math.Abs(r.PowerW) > 1 ? r.PowerW.ToString("F0") : "0")}/{r.SocWh:F0}")));
            double chg = today.Where(r => r.PowerW > 0).Sum(r => r.PowerW * 0.25) / 1000.0;
            _output.WriteLine($"  charged before 21:00: {chg:F2} kWh AC; charge quarters: " +
                string.Join(",", today.Where(r => r.PowerW > 1 && (r.Mode.StartsWith("Charg"))).Select(r => r.Time.ToString("HH:mm"))));
        }
    }
}
