using Microsoft.Data.Sqlite;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic for the night of 06-10 → 07-10: the batteries reached 0%. Lists per quarter what
    /// was planned, what ran and what the SOC did. Skips itself when the local database is not there.
    /// </summary>
    public class NightDrainProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";
        private static readonly DateTime From = new(2026, 10, 6, 17, 0, 0);
        private static readonly DateTime To = new(2026, 10, 7, 9, 0, 0);

        private readonly ITestOutputHelper _output;
        public NightDrainProbeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Probe_night_drain()
        {
            if (!File.Exists(DatabasePath)) return;

            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            using (var versions = connection.CreateCommand())
            {
                versions.CommandText = "select * from AppVersions order by 1 desc limit 8";
                using var r = versions.ExecuteReader();
                while (r.Read())
                    _output.WriteLine("AppVersion: " + string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)?.ToString())));
            }

            using (var settings = connection.CreateCommand())
            {
                settings.CommandText = "select FixedNightReservePct, UseCalculatedNightReserve, ManualOverride, ShiftDischargeEnabled from Settings";
                try
                {
                    using var r = settings.ExecuteReader();
                    while (r.Read())
                        _output.WriteLine($"Settings: reserve {r.GetValue(0)}% calc {r.GetValue(1)} manual {r.GetValue(2)} shift {r.GetValue(3)}");
                }
                catch (Exception ex) { _output.WriteLine("Settings: " + ex.Message); }
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                @"select m.Time, m.BatteryStateOfChargeWh, m.BatteryPowerWatts, m.BatteryMode,
                         a.ActualMode, a.StateMachineReason, a.ControlMode,
                         p.PlannedMode, p.PlannedDischargePowerW, p.PlannedChargePowerW, p.PlannedChargeLeftWh, p.MinSocWh, p.NetLoadWh
                  from QuarterlyMeasurements m
                  left join ActualQuarters a on a.Time = m.Time
                  left join PlannedQuarters p on p.Time = m.Time
                  where m.Time >= $from and m.Time < $to order by m.Time";
            command.Parameters.AddWithValue("$from", From.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$to", To.ToString("yyyy-MM-dd HH:mm:ss"));

            _output.WriteLine("time         soc  powerW mode | actual | plan mode  disW  chW  planSoc minSoc netLoad | reason");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string S(int i) => reader.IsDBNull(i) ? "-" : reader.GetValue(i).ToString()!;
                double D(int i) => reader.IsDBNull(i) ? double.NaN : reader.GetDouble(i);
                _output.WriteLine(
                    $"{DateTime.Parse(reader.GetString(0)):dd-MM HH:mm} {D(1),5:F0} {D(2),6:F0} {S(3),2} | {S(4),-22} {S(6),-8} | " +
                    $"{S(7),-11} {D(8),5:F0} {D(9),5:F0} {D(10),6:F0} {D(11),5:F0} {D(12),5:F0} | {S(5)}");
            }
        }
    }
}
