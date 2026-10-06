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
