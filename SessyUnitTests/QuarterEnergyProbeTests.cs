using Microsoft.Data.Sqlite;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic: do (dis)charge quarters deliver their planned energy? Compares the planned DC
    /// SOC change with the measured one (last SOC sample of the quarter minus that of the previous
    /// quarter). Skips itself when the local database is not there.
    /// </summary>
    public class QuarterEnergyProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        // Local test runs overwrite PlannedQuarters from 06-10 13:00 on.
        private static readonly DateTime End = new(2026, 10, 6, 13, 0, 0);
        private static readonly DateTime Start = End.AddDays(-21);

        // Approximate efficiencies (plan read-back 06-10).
        private const double ChargeEff = 0.87;
        private const double DischargeEff = 0.90;

        private readonly ITestOutputHelper _output;
        public QuarterEnergyProbeTests(ITestOutputHelper output) => _output = output;

        private sealed record Row(DateTime Time, string PlannedMode, double ChargeW, double DischargeW,
                                  double PrevSocWh, double SocWh, int ExecutedMode);

        private static List<Row> Read()
        {
            var rows = new List<Row>();
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                @"select p.Time, p.PlannedMode, p.PlannedChargePowerW, p.PlannedDischargePowerW,
                         prev.BatteryStateOfChargeWh, m.BatteryStateOfChargeWh, m.BatteryMode
                  from PlannedQuarters p
                  join QuarterlyMeasurements m on m.Time = p.Time
                  join QuarterlyMeasurements prev on prev.Time = datetime(p.Time, '-15 minutes')
                  where p.Time >= $start and p.Time < $end
                    and p.PlannedMode in ('Charging', 'Discharging')
                    and m.IsReliable = 1 and prev.IsReliable = 1
                  order by p.Time";
            command.Parameters.AddWithValue("$start", Start.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$end", End.ToString("yyyy-MM-dd HH:mm:ss"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new Row(DateTime.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                    reader.GetInt32(6)));
            return rows;
        }

        /// <summary>
        /// Charging quarters by SOC band: planned power, power snapshots at the start (ActualQuarter)
        /// and end (QuarterlyMeasurement) of the quarter, and the SOC change. Separates "the bank
        /// took less AC power" from "the energy did not show up in the SOC".
        /// </summary>
        [Fact]
        public void Probe_charge_power_by_soc()
        {
            if (!File.Exists(DatabasePath)) return;

            var rows = new List<(double PrevSoc, double PlanW, double StartW, double EndW, double DeltaWh)>();
            using (var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"select prev.BatteryStateOfChargeWh, p.PlannedChargePowerW, a.ActualPowerW,
                             m.BatteryPowerWatts, m.BatteryStateOfChargeWh - prev.BatteryStateOfChargeWh
                      from PlannedQuarters p
                      join QuarterlyMeasurements m on m.Time = p.Time
                      join QuarterlyMeasurements prev on prev.Time = datetime(p.Time, '-15 minutes')
                      join ActualQuarters a on a.Time = p.Time
                      where p.Time >= $start and p.Time < $end
                        and p.PlannedMode = 'Charging' and m.BatteryMode = 1
                        and m.IsReliable = 1 and prev.IsReliable = 1";
                command.Parameters.AddWithValue("$start", Start.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("$end", End.ToString("yyyy-MM-dd HH:mm:ss"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetDouble(0), reader.GetDouble(1), Math.Abs(reader.GetDouble(2)),
                              Math.Abs(reader.GetDouble(3)), reader.GetDouble(4)));
            }

            _output.WriteLine($"{rows.Count} charging quarters. W = mean power; eff = SOC gain / mean snapshot energy");
            foreach (var band in rows.GroupBy(r => Math.Min((int)(r.PrevSoc / 16200.0 * 10), 9)).OrderBy(g => g.Key))
            {
                var b = band.ToList();
                double plan = b.Average(r => r.PlanW);
                double start = b.Average(r => r.StartW);
                double end = b.Average(r => r.EndW);
                double dcW = b.Average(r => r.DeltaWh * 4.0);
                double eff = dcW / ((start + end) / 2.0);
                _output.WriteLine(
                    $"SOC {band.Key * 10,2}-{band.Key * 10 + 10,3}%: {b.Count,3} q  plan {plan,5:F0}  start {start,5:F0}  " +
                    $"end {end,5:F0}  SOC-gain {dcW,5:F0} W  end/plan {end / plan,4:P0}  eff {eff:F2}");

                // Spread of the quarter's average power (SOC gain / 0.93): limit or variation?
                var avg = b.Select(r => r.DeltaWh * 4.0 / 0.93).OrderBy(v => v).ToList();
                double P(double q) => avg[(int)Math.Min(avg.Count - 1, Math.Round(q * (avg.Count - 1)))];
                _output.WriteLine($"      avg AC p10 {P(0.1),5:F0}  p50 {P(0.5),5:F0}  p90 {P(0.9),5:F0}  max {avg[^1],5:F0}");
            }
        }

        /// <summary>
        /// Charge power at 40-80% SOC per month, from SOC gain only (no plan needed), to see
        /// whether it changed when (dis)charging moved to the P1 grid target (v1.0.12x, early Sept).
        /// </summary>
        [Fact]
        public void Probe_charge_power_history()
        {
            if (!File.Exists(DatabasePath)) return;

            var rows = new List<(DateTime Time, double GainWh, double EndW)>();
            using (var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"select m.Time, m.BatteryStateOfChargeWh - prev.BatteryStateOfChargeWh, m.BatteryPowerWatts
                      from QuarterlyMeasurements m
                      join QuarterlyMeasurements prev on prev.Time = datetime(m.Time, '-15 minutes')
                      where m.BatteryMode = 1 and prev.BatteryMode = 1
                        and m.IsReliable = 1 and prev.IsReliable = 1
                        and prev.BatteryStateOfChargeWh between 6480 and 12960
                        and m.Time < $end
                      order by m.Time";
                command.Parameters.AddWithValue("$end", End.ToString("yyyy-MM-dd HH:mm:ss"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((DateTime.Parse(reader.GetString(0)), reader.GetDouble(1), Math.Abs(reader.GetDouble(2))));
            }

            _output.WriteLine("Charging quarters (previous quarter also charging), SOC 40-80%:");
            foreach (var g in rows.GroupBy(r => new DateTime(r.Time.Year, r.Time.Month, r.Time.Day < 16 ? 1 : 16)))
            {
                var gain = g.Select(r => r.GainWh * 4.0).OrderBy(v => v).ToList();
                double P(double q) => gain[(int)Math.Min(gain.Count - 1, Math.Round(q * (gain.Count - 1)))];
                _output.WriteLine($"{g.Key:yyyy-MM-dd}: {g.Count(),4} q  SOC-gain W p10 {P(0.1),5:F0}  p50 {P(0.5),5:F0}  " +
                                  $"p90 {P(0.9),5:F0}  end snapshot mean {g.Average(r => r.EndW),5:F0}");
            }
        }

        /// <summary>What the charge taper fits on: snapshot power / PlannedUnthrottledPowerW, per SOC band.</summary>
        [Fact]
        public void Probe_taper_ratio_inputs()
        {
            if (!File.Exists(DatabasePath)) return;

            var rows = new List<(double Soc, double UnthrottledW, double PlanW, double EndW)>();
            using (var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"select m.BatteryStateOfChargeWh, p.PlannedUnthrottledPowerW, p.PlannedChargePowerW, m.BatteryPowerWatts
                      from PlannedQuarters p join QuarterlyMeasurements m on m.Time = p.Time
                      where p.Time >= $start and p.Time < $end and m.BatteryMode = 1
                        and p.PlannedUnthrottledPowerW > 0";
                command.Parameters.AddWithValue("$start", End.AddDays(-31).ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("$end", End.ToString("yyyy-MM-dd HH:mm:ss"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), Math.Abs(reader.GetDouble(3))));
            }

            _output.WriteLine($"{rows.Count} charging quarters with PlannedUnthrottledPowerW (31 days)");
            foreach (var band in rows.GroupBy(r => Math.Min((int)(r.Soc / 16200.0 * 10), 9)).OrderBy(g => g.Key))
            {
                var ratios = band.Select(r => Math.Min(r.EndW / r.UnthrottledW, 1.02)).OrderByDescending(v => v).ToList();
                _output.WriteLine(
                    $"SOC {band.Key * 10,2}%: {band.Count(),3} q  unthrottled {band.Average(r => r.UnthrottledW),5:F0}  " +
                    $"plan {band.Average(r => r.PlanW),5:F0}  end {band.Average(r => r.EndW),5:F0}  " +
                    $"ratio top {ratios[0]:F2}  median {ratios[ratios.Count / 2]:F2}");
            }
        }

        /// <summary>
        /// Discharge quarters at a full request, previous quarter discharging too: SOC drop (DC W),
        /// end snapshot and planned power per 10% SOC band, last 60 days.
        /// </summary>
        [Fact]
        public void Probe_discharge_power_by_soc()
        {
            if (!File.Exists(DatabasePath)) return;

            var rows = new List<(double PrevSoc, double PlanW, double EndW, double DropWh)>();
            using (var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"select prev.BatteryStateOfChargeWh, p.PlannedDischargePowerW, m.BatteryPowerWatts,
                             prev.BatteryStateOfChargeWh - m.BatteryStateOfChargeWh
                      from QuarterlyMeasurements m
                      join QuarterlyMeasurements prev on prev.Time = datetime(m.Time, '-15 minutes')
                      join PlannedQuarters p on p.Time = m.Time
                      where m.Time >= $start and m.Time < $end
                        and m.BatteryMode = 2 and prev.BatteryMode = 2
                        and m.IsReliable = 1 and prev.IsReliable = 1
                        and p.PlannedUnthrottledPowerW <= -0.9 * 5100";
                command.Parameters.AddWithValue("$start", End.AddDays(-60).ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("$end", End.ToString("yyyy-MM-dd HH:mm:ss"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetDouble(0), reader.GetDouble(1), Math.Abs(reader.GetDouble(2)), reader.GetDouble(3)));
            }

            _output.WriteLine($"{rows.Count} sustained full-request discharge quarters (60 days)");
            foreach (var band in rows.GroupBy(r => Math.Min((int)(r.PrevSoc / 16200.0 * 10), 9)).OrderBy(g => g.Key))
            {
                var drop = band.Select(r => r.DropWh * 4.0).OrderBy(v => v).ToList();
                var end = band.Select(r => r.EndW).OrderBy(v => v).ToList();
                _output.WriteLine(
                    $"SOC {band.Key * 10,2}%: {band.Count(),3} q  plan {band.Average(r => r.PlanW),5:F0}  " +
                    $"SOC-drop DC p50 {drop[drop.Count / 2],5:F0} (p10 {drop[drop.Count / 10],5:F0}, p90 {drop[drop.Count * 9 / 10],5:F0})  " +
                    $"end snapshot p50 {end[end.Count / 2],5:F0} max {end[^1],5:F0}");
            }
        }

        [Fact]
        public void Probe_planned_vs_measured_quarter_energy()
        {
            if (!File.Exists(DatabasePath)) return;

            var rows = Read();
            _output.WriteLine($"{rows.Count} (dis)charge quarters {Start:dd-MM} .. {End:dd-MM HH:mm}");

            foreach (var group in rows.GroupBy(r => r.PlannedMode))
            {
                bool charging = group.Key == "Charging";
                double nameplateW = charging ? 6600.0 : 5100.0;

                // Planned and measured |SOC change| in Wh (DC side).
                var items = group.Select(r =>
                {
                    double plannedWh = charging
                        ? r.ChargeW * 0.25 * ChargeEff
                        : r.DischargeW * 0.25 / DischargeEff;
                    double measuredWh = charging ? r.SocWh - r.PrevSocWh : r.PrevSocWh - r.SocWh;
                    double plannedW = charging ? r.ChargeW : r.DischargeW;
                    return (r, plannedW, plannedWh, measuredWh, partial: plannedW < 0.8 * nameplateW);
                }).Where(x => x.plannedWh > 50).ToList();

                // Executed as planned only (1 = Charging, 2 = Discharging); guards are not execution loss.
                int expectedMode = charging ? 1 : 2;
                int overridden = items.Count(x => x.r.ExecutedMode != expectedMode);
                _output.WriteLine($"{group.Key}: {overridden} of {items.Count} quarters executed in another mode (excluded below)");
                items = items.Where(x => x.r.ExecutedMode == expectedMode).ToList();

                // By SOC band: taper/knee versus regulation.
                foreach (var band in items.GroupBy(x => (int)(x.r.PrevSocWh / 16200.0 * 5)).OrderBy(g => g.Key))
                {
                    double p = band.Sum(x => x.plannedWh), m = band.Sum(x => x.measuredWh);
                    _output.WriteLine($"   SOC {band.Key * 20,3}-{band.Key * 20 + 20}%: {band.Count(),4} q, " +
                                      $"partial {band.Count(x => x.partial),3}, measured/planned {m / p:P0}");
                }

                foreach (var bucket in items.GroupBy(x => x.partial))
                {
                    var b = bucket.ToList();
                    double planned = b.Sum(x => x.plannedWh);
                    double measured = b.Sum(x => x.measuredWh);
                    int under = b.Count(x => x.measuredWh < 0.9 * x.plannedWh);
                    double shortfall = b.Sum(x => Math.Max(0.0, x.plannedWh - x.measuredWh));
                    double surplus = b.Sum(x => Math.Max(0.0, x.measuredWh - x.plannedWh));

                    _output.WriteLine(
                        $"{group.Key,-11} {(bucket.Key ? "partial" : "full   ")}: {b.Count,4} q, planned {planned / 1000:F1} kWh, " +
                        $"measured {measured / 1000:F1} kWh ({measured / planned:P0}), <90%: {under} q, " +
                        $"shortfall {shortfall / 1000:F1} kWh, surplus {surplus / 1000:F1} kWh");
                }

                // Worst partial quarters, to look at by hand.
                foreach (var x in items.Where(x => x.partial).OrderBy(x => x.measuredWh - x.plannedWh).Take(10))
                    _output.WriteLine(
                        $"   {x.r.Time:dd-MM HH:mm} plan {x.plannedW,5:F0} W  planned {x.plannedWh,5:F0} Wh  " +
                        $"measured {x.measuredWh,5:F0} Wh  executed mode {x.r.ExecutedMode}  SOC {x.r.SocWh:F0}");
            }

            // Per day, to see whether it changed with the P1 grid target.
            foreach (var day in rows.GroupBy(r => r.Time.Date))
            {
                double planned = day.Sum(r => r.PlannedMode == "Charging" ? r.ChargeW * 0.25 * ChargeEff : r.DischargeW * 0.25 / DischargeEff);
                double measured = day.Sum(r => r.PlannedMode == "Charging" ? r.SocWh - r.PrevSocWh : r.PrevSocWh - r.SocWh);
                _output.WriteLine($"{day.Key:dd-MM}: {day.Count(),3} q, planned {planned / 1000:F1} kWh, measured {measured / 1000:F1} kWh ({(planned > 0 ? measured / planned : 0):P0})");
            }
        }
    }
}
