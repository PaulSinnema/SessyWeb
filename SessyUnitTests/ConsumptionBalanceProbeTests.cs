using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic: stored Consumption per day against the energy balance from the other tables
    /// (grid import − export from EnergyHistory, battery from QuarterlyMeasurements, solar from
    /// InverterMeasurements). Writes ConsumptionBalance.txt next to the test assembly. Skips without the local database.
    /// </summary>
    public class ConsumptionBalanceProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";
        private const int Days = 10;

        private readonly ITestOutputHelper _output;
        public ConsumptionBalanceProbeTests(ITestOutputHelper output) => _output = output;

        private sealed class Quarter
        {
            public double ConsumptionW;
            public bool HasConsumption;
            public double ImportWh, ExportWh, BatteryW, SolarKWh;
        }

        [Fact]
        public void Probe_consumption_balance()
        {
            if (!File.Exists(DatabasePath))
            {
                _output.WriteLine("Skipped: no local database.");
                return;
            }

            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            var last = DateTime.Parse(Scalar(connection, "select max(Time) from Consumption")!, CultureInfo.InvariantCulture).Date;
            var from = last.AddDays(-Days + 1);
            var quarters = new SortedDictionary<DateTime, Quarter>();
            Quarter Q(DateTime t) => quarters.TryGetValue(t, out var q) ? q : quarters[t] = new Quarter();

            foreach (var (time, value) in Rows(connection, "select Time, ConsumptionWh from Consumption where Time >= $from", from))
            {
                var q = Q(time); q.ConsumptionW = value; q.HasConsumption = true;
            }

            foreach (var (time, value) in Rows(connection, "select Time, BatteryPowerWatts from QuarterlyMeasurements where Time >= $from", from))
                Q(time).BatteryW = value;

            foreach (var (time, value) in Rows(connection, "select Time, sum(SolarProductionKWh) from InverterMeasurements where Time >= $from group by Time", from))
                Q(time).SolarKWh = value;

            // Grid deltas per meter between consecutive readings, booked on the later reading.
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"select Time, MeterId, ConsumedTariff1 + ConsumedTariff2, ProducedTariff1 + ProducedTariff2
                                        from EnergyHistory where Time >= $from order by MeterId, Time";
                command.Parameters.AddWithValue("$from", from.AddMinutes(-15).ToString("yyyy-MM-dd HH:mm:ss"));
                using var reader = command.ExecuteReader();
                string? meter = null; double prevIn = 0, prevOut = 0;
                while (reader.Read())
                {
                    var time = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                    var id = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    double inWh = reader.GetDouble(2), outWh = reader.GetDouble(3);
                    if (id == meter && time >= from)
                    {
                        var q = Q(time);
                        q.ImportWh += Math.Max(0, inWh - prevIn);
                        q.ExportWh += Math.Max(0, outWh - prevOut);
                    }
                    meter = id; prevIn = inWh; prevOut = outWh;
                }
            }

            var report = new StringBuilder();
            report.AppendLine("Day          Quarters  Stored kWh  Import  Export  Battery(out−in)  Solar  Balance kWh  Stored/Balance");

            foreach (var day in quarters.GroupBy(q => q.Key.Date))
            {
                var list = day.Select(d => d.Value).ToList();
                double stored = list.Where(q => q.HasConsumption).Sum(q => q.ConsumptionW) * 0.25 / 1000;
                double import = list.Sum(q => q.ImportWh) / 1000, export = list.Sum(q => q.ExportWh) / 1000;
                double battery = list.Sum(q => q.BatteryW) * 0.25 / 1000, solar = list.Sum(q => q.SolarKWh);
                double balance = import - export + battery + solar;
                report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{day.Key:yyyy-MM-dd}  {list.Count(q => q.HasConsumption),8}  {stored,10:0.00}  {import,6:0.00}  {export,6:0.00}  {battery,15:0.00}  {solar,5:0.00}  {balance,11:0.00}  {(balance > 0 ? stored / balance * 100 : 0),13:0}%"));
            }

            // Hour by hour for the last day.
            report.AppendLine();
            report.AppendLine($"Hours of {last:yyyy-MM-dd}: stored W | import W | export W | battery W | solar W | balance W");
            foreach (var hour in quarters.Where(q => q.Key.Date == last).GroupBy(q => q.Key.Hour))
            {
                var list = hour.Select(h => h.Value).ToList();
                double n = list.Count;
                report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{hour.Key:00}:00  {list.Where(q => q.HasConsumption).Select(q => q.ConsumptionW).DefaultIfEmpty().Average(),7:0}  {list.Sum(q => q.ImportWh) * 4 / n,7:0}  {list.Sum(q => q.ExportWh) * 4 / n,7:0}  {list.Average(q => q.BatteryW),7:0}  {list.Sum(q => q.SolarKWh) * 4000 / n,7:0}  {(list.Sum(q => q.ImportWh - q.ExportWh + q.SolarKWh * 1000) * 4 / n + list.Average(q => q.BatteryW)),7:0}"));
            }

            var path = Path.Combine(AppContext.BaseDirectory, "ConsumptionBalance.txt");
            File.WriteAllText(path, report.ToString());
            _output.WriteLine(report.ToString());
        }

        private static string? Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar()?.ToString();
        }

        private static IEnumerable<(DateTime Time, double Value)> Rows(SqliteConnection connection, string sql, DateTime from)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                yield return (DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.IsDBNull(1) ? 0 : reader.GetDouble(1));
        }
    }
}
