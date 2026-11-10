using Microsoft.Data.Sqlite;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Throwaway diagnostic: dumps the data needed for the Frank Energie imbalance-market
    /// business case (battery investments, cycle-cost settings, recent financial results).
    /// Delete after use.
    /// </summary>
    public class BusinessCaseProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        private readonly ITestOutputHelper _output;
        public BusinessCaseProbeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Dump_investment_and_cyclecost_settings()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            _output.WriteLine("== Investments ==");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"select Id, Description, PurchaseDate, AmountEur, SubsidyEur,
                                            ExpectedLifetimeYears, CapacityWh, ExpectedTotalCycles,
                                            InvestmentGroupId
                                     from Investment order by PurchaseDate";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    _output.WriteLine(
                        $"Id={reader.GetInt32(0)} Desc={reader.GetString(1)} " +
                        $"Date={reader.GetString(2)} Amount={reader.GetDouble(3):F2} Subsidy={reader.GetDouble(4):F2} " +
                        $"LifetimeYrs={reader.GetInt32(5)} CapacityWh={reader.GetDouble(6):F0} " +
                        $"ExpectedCycles={reader.GetInt32(7)} GroupId={(reader.IsDBNull(8) ? "null" : reader.GetInt32(8).ToString())}");
                }
            }

            _output.WriteLine("");
            _output.WriteLine("== InvestmentGroups ==");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "select Id, Name, Category from InvestmentGroups order by Id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    _output.WriteLine($"Id={reader.GetInt32(0)} Name={reader.GetString(1)} Category={reader.GetInt32(2)}");
            }

            _output.WriteLine("");
            _output.WriteLine("== Settings (cycle cost + related) ==");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"select UseCalculatedCycleCost, FixedCycleCostEurPerKWh,
                                            RoundTripEfficiencyFallbackPct, PlanningHorizonHours
                                     from Settings limit 1";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    _output.WriteLine(
                        $"UseCalculatedCycleCost={reader.GetBoolean(0)} FixedCycleCostEurPerKWh={reader.GetDouble(1):F4} " +
                        $"RoundTripEfficiencyFallbackPct={reader.GetDouble(2):F1} PlanningHorizonHours={reader.GetInt32(3)}");
                }
            }

            _output.WriteLine("");
            _output.WriteLine("== Battery throughput last 365 days (QuarterlyMeasurements) ==");
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"select min(Time), max(Time), count(*) from QuarterlyMeasurements
                                     where Time >= datetime('now', '-365 days')";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    _output.WriteLine($"MinTime={reader.GetValue(0)} MaxTime={reader.GetValue(1)} RowCount={reader.GetInt64(2)}");
            }

            Assert.True(true);
        }
    }
}
