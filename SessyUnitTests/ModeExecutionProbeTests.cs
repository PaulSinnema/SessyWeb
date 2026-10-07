using Microsoft.Data.Sqlite;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic: how planned modes were actually executed, and where the SOC went below the
    /// planned reserve. Skips itself when the local database is not there.
    /// </summary>
    public class ModeExecutionProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        private readonly ITestOutputHelper _output;
        public ModeExecutionProbeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Probe_planned_vs_actual_modes()
        {
            if (!File.Exists(DatabasePath)) return;

            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            using (var settings = connection.CreateCommand())
            {
                settings.CommandText = "select * from Settings";
                using var r = settings.ExecuteReader();
                while (r.Read())
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        var name = r.GetName(i);
                        if (name.Contains("Cycle") || name.Contains("Reserve") || name.Contains("Shift") || name.Contains("Manual"))
                            _output.WriteLine($"Setting {name} = {r.GetValue(i)}");
                    }
            }

            var from = DateTime.Now.Date.AddDays(-14).ToString("yyyy-MM-dd HH:mm:ss");

            using (var pairs = connection.CreateCommand())
            {
                pairs.CommandText =
                    @"select p.PlannedMode, a.ActualMode, count(*),
                             sum(case when m.BatteryStateOfChargeWh < p.MinSocWh - 50 then 1 else 0 end)
                      from PlannedQuarters p
                      join ActualQuarters a on a.Time = p.Time
                      left join QuarterlyMeasurements m on m.Time = p.Time
                      where p.Time >= $from
                      group by p.PlannedMode, a.ActualMode
                      order by p.PlannedMode, count(*) desc";
                pairs.Parameters.AddWithValue("$from", from);

                _output.WriteLine("planned      actual                 count  socBelowMin");
                using var r = pairs.ExecuteReader();
                while (r.Read())
                    _output.WriteLine($"{r.GetValue(0),-12} {r.GetValue(1),-22} {r.GetValue(2),5}  {r.GetValue(3),5}");
            }

            using (var below = connection.CreateCommand())
            {
                below.CommandText =
                    @"select p.Time, p.PlannedMode, a.StateMachineReason, m.BatteryStateOfChargeWh, p.MinSocWh,
                             p.PlannedChargeLeftWh, p.NetLoadWh, m.BatteryPowerWatts
                      from PlannedQuarters p
                      join ActualQuarters a on a.Time = p.Time
                      join QuarterlyMeasurements m on m.Time = p.Time
                      where p.Time >= $from and m.BatteryStateOfChargeWh < p.MinSocWh - 50
                      order by p.Time";
                below.Parameters.AddWithValue("$from", from);

                _output.WriteLine("");
                _output.WriteLine("SOC below planned reserve: time  planned  soc  minSoc  planSoc  netLoad  batW | reason");
                using var r = below.ExecuteReader();
                while (r.Read())
                    _output.WriteLine($"{DateTime.Parse(r.GetString(0)):dd-MM HH:mm} {r.GetValue(1),-11} {r.GetDouble(3),6:F0} {r.GetDouble(4),6:F0} {r.GetDouble(5),6:F0} {r.GetDouble(6),6:F0} {r.GetDouble(7),6:F0} | {r.GetValue(2)}");
            }

            using (var plans = connection.CreateCommand())
            {
                plans.CommandText =
                    @"select SavedAt, Reason, min(ObjectiveEur), count(*)
                      from PlannedActions
                      where SavedAt >= $from2
                      group by PlanId order by SavedAt";
                plans.Parameters.AddWithValue("$from2", DateTime.Now.Date.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss"));

                _output.WriteLine("");
                _output.WriteLine("plans saved (last 2 days):");
                try
                {
                    using var r = plans.ExecuteReader();
                    while (r.Read())
                        _output.WriteLine($"{r.GetValue(0)} obj {r.GetValue(2)} n {r.GetValue(3)} | {r.GetValue(1)}");
                }
                catch (Exception ex) { _output.WriteLine("PlannedActions: " + ex.Message); }
            }

            using (var reasons = connection.CreateCommand())
            {
                reasons.CommandText =
                    @"select p.PlannedMode, a.ActualMode, a.StateMachineReason, count(*)
                      from PlannedQuarters p
                      join ActualQuarters a on a.Time = p.Time
                      where p.Time >= $from and p.PlannedMode <> a.ActualMode
                      group by p.PlannedMode, a.ActualMode, a.StateMachineReason
                      order by count(*) desc limit 40";
                reasons.Parameters.AddWithValue("$from", from);

                _output.WriteLine("");
                _output.WriteLine("mismatches by reason:");
                using var r = reasons.ExecuteReader();
                while (r.Read())
                    _output.WriteLine($"{r.GetValue(0),-12} → {r.GetValue(1),-22} {r.GetValue(3),5}  {r.GetValue(2)}");
            }
        }
    }
}
