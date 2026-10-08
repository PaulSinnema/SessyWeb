using Microsoft.Data.Sqlite;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic for the 08-10 09:08 plan: evening sells at 17:30 while 19:00-21:30 pays more,
    /// and the afternoon charges only ~8.6 kWh. Replays the stored inputs with variations.
    /// Skips without the local database.
    /// </summary>
    public class EveningPeakProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";
        private static readonly DateTime HorizonStart = new(2026, 10, 8, 9, 30, 0);
        private const double CapacityKWh = 16.2;

        private readonly ITestOutputHelper _output;
        public EveningPeakProbeTests(ITestOutputHelper output) => _output = output;

        private sealed record Quarter(DateTime Time, string Mode, double PlanW, double LeftWh,
                                      double Buy, double Sell, double NetLoadWh, double MinSocWh);

        [Fact]
        public void Probe_evening_peak()
        {
            if (!File.Exists(DatabasePath)) return;

            var quarters = ReadPlan();
            var points = quarters.Select(q => new PricePoint(q.Time, q.Buy, q.Sell, q.NetLoadWh,
                Math.Max(0.0, -q.NetLoadWh))).ToList();
            var bounds = quarters.Select(q => new SocBound(q.Time, q.MinSocWh / 1000.0, CapacityKWh)).ToList();

            var curve = new EfficiencyCurve(ChargeCeiling: 0.8796, ChargeOverheadKW: 0.070,
                                            DischargeCeiling: 0.915, DischargeOverheadKW: 0.071, Samples: 100);
            var envelope = ThrottleAnalysisService.FitDischargeCapability(Samples(2), 5100.0);
            var capability = ThrottleAnalysisService.WithSustainedPlateau(envelope, SustainedDischargeSamples());
            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(Samples(1), 6600.0);
            var chargeCapability = ThrottleAnalysisService.FitChargeCapability(SustainedChargeSamples());
            _output.WriteLine($"discharge capability: plateau {capability.PlateauW:F0} W, knee {capability.KneeSoc:P0} (envelope {envelope.PlateauW:F0} W)");
            _output.WriteLine($"charge capability (DC W) per bin: {string.Join(" ", chargeCapability.BinPowerW.Select(w => w.ToString("F0")))}");

            BatterySpec Spec(DischargeCapability? cap) =>
                new(CapacityKWh, 0.0, 6.6, 5.1, 0.87, 0.90, ChargeTaper: null, Efficiency: curve,
                    DischargeCapability: cap, ChargeFloor: floor, ChargeCapability: chargeCapability);

            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true,
                FutureValueDiscountPerHour: 0.003, ReservationPriceEurPerKWh: 0.20, AllowCarryForward: true,
                AllowShift: true);

            Show("PRODUCTION", quarters.Select(q => (q.Time, q.Mode, q.PlanW, q.LeftWh)).ToList(), null);

            Run("reproduction", opt, Spec(capability), points, bounds);
            Run("no shift", opt with { AllowShift = false }, Spec(capability), points, bounds);
            Run("no discharge capability (no knee)", opt, Spec(null), points, bounds);
            Run("no discount", opt with { FutureValueDiscountPerHour = 0.0 }, Spec(capability), points, bounds);
            Run("no carry-forward", opt with { AllowCarryForward = false }, Spec(capability), points, bounds);
            Run("today only", opt, Spec(capability),
                points.Where(p => p.Start.Date == HorizonStart.Date).ToList(),
                bounds.Where(b => b.Time.Date == HorizonStart.Date).ToList());
        }

        [Fact]
        public void Probe_input_grid()
        {
            if (!File.Exists(DatabasePath)) return;

            var quarters = ReadPlan();
            var points = quarters.Select(q => new PricePoint(q.Time, q.Buy, q.Sell, q.NetLoadWh,
                Math.Max(0.0, -q.NetLoadWh))).ToList();

            var curve = new EfficiencyCurve(ChargeCeiling: 0.8796, ChargeOverheadKW: 0.070,
                                            DischargeCeiling: 0.915, DischargeOverheadKW: 0.071, Samples: 100);
            var envelope = ThrottleAnalysisService.FitDischargeCapability(Samples(2), 5100.0);
            var capability = ThrottleAnalysisService.WithSustainedPlateau(envelope, SustainedDischargeSamples());
            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(Samples(1), 6600.0);
            var chargeCapability = ThrottleAnalysisService.FitChargeCapability(SustainedChargeSamples());

            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true,
                FutureValueDiscountPerHour: 0.003, ReservationPriceEurPerKWh: 0.20, AllowCarryForward: true,
                AllowShift: true);

            void Try(string label, double cap, EfficiencyCurve? eff, DischargeCapability? dc, ChargeCapability? cc,
                     SessyOptions o, bool clampMin = false)
            {
                var bounds = quarters.Select(q => new SocBound(q.Time, clampMin ? 0.0 : Math.Min(cap, q.MinSocWh / 1000.0), cap)).ToList();
                var spec = new BatterySpec(cap, 0.0, 6.6, 5.1, 0.87, 0.90, ChargeTaper: null, Efficiency: eff,
                    DischargeCapability: dc, ChargeFloor: floor, ChargeCapability: cc);
                var r = BatteryGreedyPlanner.Solve(points, spec, o, bounds)!;
                var day = r.Plan.Where(p => p.Start >= HorizonStart.Date.AddHours(12) && p.Start < HorizonStart.Date.AddHours(23)).ToList();
                double chg = day.Sum(p => p.ChargeKW * 0.25);
                double peak = day.Max(p => p.SocEndKWh);
                var firstDis = day.FirstOrDefault(p => p.DischargeKW > 1.0);
                _output.WriteLine($"{label,-40} obj {r.ObjectiveEur,8:F4}  charged {chg,5:F2}  peak {peak,5:F2}  first big discharge {firstDis?.Start:HH:mm}");
            }

            Try("baseline", 16.2, curve, capability, chargeCapability, opt);
            Try("min clamped 0", 16.2, curve, capability, chargeCapability, opt, clampMin: true);
            Try("min clamped 0, no shift", 16.2, curve, capability, chargeCapability, opt with { AllowShift = false }, clampMin: true);
            Try("min clamped 0, no knee", 16.2, curve, null, chargeCapability, opt, clampMin: true);
            Try("flat efficiency (null curve)", 16.2, null, capability, chargeCapability, opt);
            Try("no discharge capability", 16.2, curve, null, chargeCapability, opt);
            Try("no charge capability", 16.2, curve, capability, null, opt);
            Try("envelope capability", 16.2, curve, envelope, chargeCapability, opt);
            foreach (double rc in new[] { 0.0, 0.10, 0.30, 0.40 })
                Try($"replacement {rc:F2}", 16.2, curve, capability, chargeCapability, opt with { ReservationPriceEurPerKWh = rc });
            foreach (double cyc in new[] { 0.02, 0.05, 0.0862 })
                Try($"cycle cost {cyc:F4}", 16.2, curve, capability, chargeCapability, opt with { CycleCostEurPerKWh = cyc });
            foreach (double cap in new[] { 5.4, 8.1, 10.8, 12.15 })
                Try($"capacity {cap:F2}", cap, curve, capability, chargeCapability, opt);
            Try("discount 0.03", 16.2, curve, capability, chargeCapability, opt with { FutureValueDiscountPerHour = 0.03 });
            Try("no export", 16.2, curve, capability, chargeCapability, opt with { AllowExport = false });
        }

        [Fact]
        public void Probe_trace_min_clamped()
        {
            if (!File.Exists(DatabasePath)) return;

            var quarters = ReadPlan();
            var points = quarters.Select(q => new PricePoint(q.Time, q.Buy, q.Sell, q.NetLoadWh,
                Math.Max(0.0, -q.NetLoadWh))).ToList();
            var bounds = quarters.Select(q => new SocBound(q.Time, 0.0, CapacityKWh)).ToList();

            var curve = new EfficiencyCurve(ChargeCeiling: 0.8796, ChargeOverheadKW: 0.070,
                                            DischargeCeiling: 0.915, DischargeOverheadKW: 0.071, Samples: 100);
            var envelope = ThrottleAnalysisService.FitDischargeCapability(Samples(2), 5100.0);
            var capability = ThrottleAnalysisService.WithSustainedPlateau(envelope, SustainedDischargeSamples());
            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(Samples(1), 6600.0);
            var chargeCapability = ThrottleAnalysisService.FitChargeCapability(SustainedChargeSamples());
            var spec = new BatterySpec(CapacityKWh, 0.0, 6.6, 5.1, 0.87, 0.90, ChargeTaper: null, Efficiency: curve,
                DischargeCapability: capability, ChargeFloor: floor, ChargeCapability: chargeCapability);
            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true,
                FutureValueDiscountPerHour: 0.003, ReservationPriceEurPerKWh: 0.20, AllowCarryForward: true,
                AllowShift: true);

            var lines = new List<string>();
            var r = BatteryGreedyPlanner.Solve(points, spec, opt, bounds, lines.Add)!;
            foreach (var l in lines.Where(l => l.StartsWith("knee"))) _output.WriteLine(l);
            _output.WriteLine($"iterations {lines.Count(l => l.StartsWith("iter"))}, obj {r.ObjectiveEur:F4}");
            Show("greedy final", r.Plan.Select(p => (p.Start, p.Mode.ToString(),
                p.ChargeKW > 0 ? p.ChargeKW * 1000 : -p.DischargeKW * 1000, p.SocEndKWh * 1000)).ToList(), points);

            var dp = BatteryDpPlanner.Solve(points, spec, opt, bounds)!;
            Show($"DP  obj {dp.ObjectiveEur:F4}", dp.Plan.Select(p => (p.Start, p.Mode.ToString(),
                p.ChargeKW > 0 ? p.ChargeKW * 1000 : -p.DischargeKW * 1000, p.SocEndKWh * 1000)).ToList(), points);
        }

        [Fact]
        public void Probe_knee_violations()
        {
            if (!File.Exists(DatabasePath)) return;

            var cap = new DischargeCapability(3676, 0.20, 20);
            var curve = new EfficiencyCurve(ChargeCeiling: 0.8796, ChargeOverheadKW: 0.070,
                                            DischargeCeiling: 0.915, DischargeOverheadKW: 0.071, Samples: 100);
            foreach (var start in new[] { new DateTime(2026, 10, 6, 17, 15, 0), new DateTime(2026, 10, 7, 13, 30, 0), HorizonStart })
            foreach (bool shift in new[] { true, false })
            {
                var rows = ReadRows(start, start.AddHours(30));
                var points = rows.Select(q => new PricePoint(q.Time, q.Buy, q.Sell, q.NetLoadWh, Math.Max(0.0, -q.NetLoadWh))).ToList();
                var bounds = rows.Select(q => new SocBound(q.Time, q.MinSocWh / 1000.0, CapacityKWh)).ToList();
                double soc0 = start == HorizonStart ? 0.0 : (rows[0].LeftWh + rows[0].DisW * 0.25 / 0.9) / 1000.0;
                var spec = new BatterySpec(CapacityKWh, soc0, 6.6, 5.1, 0.87, 0.90, Efficiency: curve, DischargeCapability: cap);
                var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true,
                    FutureValueDiscountPerHour: 0.003, ReservationPriceEurPerKWh: 0.20, AllowCarryForward: true, AllowShift: shift);
                var r = BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;

                int violations = 0; double overKWh = 0.0;
                foreach (var p in r.Plan)
                {
                    double deliverable = cap.PowerW(p.SocStartKWh / CapacityKWh) / 1000.0 * 0.25;
                    double dis = p.DischargeKW * 0.25;
                    if (dis > deliverable + 0.005) { violations++; overKWh += dis - deliverable; }
                }
                _output.WriteLine($"{start:dd-MM HH:mm} shift {shift,-5} obj {r.ObjectiveEur,8:F4}  knee violations {violations} ({overKWh:F2} kWh undeliverable)");
            }
        }

        private static List<(DateTime Time, double Buy, double Sell, double NetLoadWh, double MinSocWh, double LeftWh, double DisW)> ReadRows(DateTime start, DateTime end)
        {
            var rows = new List<(DateTime, double, double, double, double, double, double)>();
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                @"select Time, BuyingPriceEurKWh, SellingPriceEurKWh, NetLoadWh, MinSocWh, PlannedChargeLeftWh, PlannedDischargePowerW
                  from PlannedQuarters where Time >= $start and Time < $end order by Time";
            command.Parameters.AddWithValue("$start", start.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$end", end.ToString("yyyy-MM-dd HH:mm:ss"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((DateTime.Parse(reader.GetString(0)), reader.GetDouble(1), reader.GetDouble(2),
                          reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)));
            return rows;
        }

        [Fact]
        public void Probe_charge_capability_case()
        {
            const int cheap = 40;
            var points = Enumerable.Range(0, 96).Select(i => new PricePoint(new DateTime(2026, 10, 6).AddMinutes(15 * i),
                i == cheap ? 0.05 : 0.40, i > cheap ? 0.38 : 0.02, 0, 0)).ToList();
            var bins = new double[10]; bins[6] = 3300.0;
            var spec = new BatterySpec(16.2, 11.0, 6.6, 5.1, 0.92, 0.92, ChargeTaper: null, Efficiency: null, DischargeCapability: null,
                ChargeFloor: new ChargeCapabilityFloor(Enumerable.Repeat(5300.0, 20).ToArray(), 500),
                ChargeCapability: new ChargeCapability(bins, 100));
            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.05, AllowExport: true);
            var bounds = points.Select(p => new SocBound(p.Start, 0.0, 16.2)).ToList();
            var lines = new List<string>();
            var r = BatteryGreedyPlanner.Solve(points, spec, opt, bounds, lines.Add)!;
            foreach (var l in lines.Where(l => l.StartsWith("knee") || l.StartsWith("dp"))) _output.WriteLine(l);
            var dp = BatteryDpPlanner.Solve(points, spec, opt, bounds, greedyRules: true)!;
            for (int i = 36; i < 46; i++)
                _output.WriteLine($"{i}: final c {r.Plan[i].ChargeKW:F2} d {r.Plan[i].DischargeKW:F2} soc {r.Plan[i].SocStartKWh:F2}->{r.Plan[i].SocEndKWh:F2} | dp c {dp.Plan[i].ChargeKW:F2} d {dp.Plan[i].DischargeKW:F2} soc {dp.Plan[i].SocStartKWh:F2}");
        }

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

        private static List<(double Soc, double PowerW)> SustainedDischargeSamples()
        {
            var samples = new List<(double, double)>();
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                @"select prev.BatteryStateOfChargeWh, abs(m.BatteryPowerWatts)
                  from QuarterlyMeasurements m
                  join QuarterlyMeasurements prev on prev.Time = datetime(m.Time, '-15 minutes')
                  join PlannedQuarters p on p.Time = m.Time
                  where m.Time >= $start and m.Time < $end
                    and m.BatteryMode = 2 and prev.BatteryMode = 2
                    and m.IsReliable = 1 and prev.IsReliable = 1
                    and p.PlannedUnthrottledPowerW <= -0.9 * 5100";
            command.Parameters.AddWithValue("$start", HorizonStart.AddDays(-60).ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$end", HorizonStart.ToString("yyyy-MM-dd HH:mm:ss"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                samples.Add((reader.GetDouble(0) / (CapacityKWh * 1000.0), reader.GetDouble(1)));
            return samples;
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
                    p.ChargeKW > 0 ? p.ChargeKW * 1000 : -p.DischargeKW * 1000, p.SocEndKWh * 1000)).ToList(), points);
        }

        private void Show(string label, List<(DateTime Time, string Mode, double PowerW, double SocWh)> rows, List<PricePoint>? points)
        {
            _output.WriteLine("");
            _output.WriteLine(label);
            var day = rows.Where(r => r.Time >= HorizonStart.Date.AddHours(12) && r.Time < HorizonStart.Date.AddHours(23)).ToList();
            _output.WriteLine("  " + string.Join(" ", day.Select(r => $"{r.Time:HHmm}:{ShortMode(r.Mode)}{(Math.Abs(r.PowerW) > 1 ? r.PowerW.ToString("F0") : "0")}/{r.SocWh:F0}")));
            double chg = day.Where(r => r.PowerW > 0).Sum(r => r.PowerW * 0.25) / 1000.0;
            double dis = day.Where(r => r.PowerW < 0).Sum(r => -r.PowerW * 0.25) / 1000.0;
            _output.WriteLine($"  12-23h: charged {chg:F2} kWh AC, discharged {dis:F2} kWh AC, peak SOC {day.Max(r => r.SocWh):F0} Wh");
        }

        private static string ShortMode(string mode) => mode switch
        {
            "Charging" or "Charge" => "C",
            "Discharging" or "Discharge" => "D",
            "ZeroNetHome" => "Z",
            "SolarOnly" => "S",
            _ => mode.Substring(0, 1)
        };
    }
}
