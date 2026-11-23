using SessyController.Services.Statistics;
using SessyData.Model;
using Xunit;
using SessyController.Services;

namespace SessyTests.Services
{
    public class CycleHistoryServiceTests
    {
        private static readonly DateTime Day = new(2026, 1, 1);

        [Fact]
        public void GridDeltas_with_maxInterval_skips_gaps()
        {
            var histories = new[]
            {
                new EnergyHistory { Time = Day, MeterId = "P1", ConsumedTariff1 = 1000 },
                new EnergyHistory { Time = Day.AddMinutes(15), MeterId = "P1", ConsumedTariff1 = 1200 },
                new EnergyHistory { Time = Day.AddHours(5), MeterId = "P1", ConsumedTariff1 = 9000 }
            };

            var deltas = QuarterlyFactsService.GridDeltas(histories, maxInterval: TimeSpan.FromMinutes(15));

            var delta = Assert.Single(deltas);
            Assert.Equal(Day.AddMinutes(15), delta.Key);
            Assert.Equal(200.0, delta.Value.importWh, 6);
        }

        [Fact]
        public void Reconstruct_takes_the_battery_as_the_remainder_of_the_balance()
        {
            // 2000 W house = 0.5 kWh; 0.1 kWh solar; 0.1 kWh import → battery delivered 0.3 kWh.
            var t = Day.AddMinutes(15);
            var consumption = new Dictionary<DateTime, double> { [t.AddSeconds(29)] = 2000.0 };
            var solar = new Dictionary<DateTime, double> { [t] = 0.1 };
            var grid = new Dictionary<DateTime, (double, double)> { [t] = (150.0, 50.0), [t.AddMinutes(15)] = (500.0, 0.0) };

            var result = CycleHistoryService.Reconstruct(consumption, solar, grid);

            var quarter = Assert.Single(result);
            Assert.Equal(t, quarter.Key);
            Assert.Equal(0.3, quarter.Value, 6);
        }

        [Fact]
        public void Build_prefers_measured_then_reconstructed_then_estimate()
        {
            var battery = new CycleBattery("1", "Sessy 1", 4.0, Day);
            var measured = new Dictionary<DateTime, double> { [Day] = 1.0, [Day.AddMinutes(15)] = 0.0 };   // 0.125 cycles/quarter on average
            var reconstructed = new Dictionary<DateTime, double> { [Day] = 3.0, [Day.AddMinutes(30)] = 2.0 };

            var result = CycleHistoryService.Build(measured, reconstructed, new Dictionary<string, Dictionary<DateTime, double>>(),
                [battery], Day.AddMinutes(45));

            var month = Assert.Single(result.Total.Months);
            Assert.Equal(0.25, month.MeasuredCycles, 6);        // measured wins over the reconstruction at 00:00
            Assert.Equal(0.5, month.ReconstructedCycles, 6);
            Assert.Equal(0.125, month.EstimatedCycles, 6);      // 00:45 has no data
            Assert.Equal(0.75, result.Total.TotalCycles(withEstimate: false), 6);
            Assert.Equal(0.875, result.Total.TotalCycles(withEstimate: true), 6);
        }

        [Fact]
        public void Build_per_battery_uses_own_readings_and_follows_the_bank_before_them()
        {
            var first = new CycleBattery("1", "Sessy 1", 4.0, Day);
            var second = new CycleBattery("2", "Sessy 2", 4.0, Day.AddMinutes(15));
            var measured = new Dictionary<DateTime, double> { [Day] = 2.0, [Day.AddMinutes(15)] = 4.0 };
            var perBattery = new Dictionary<string, Dictionary<DateTime, double>> { ["1"] = new() { [Day.AddMinutes(15)] = 1.0 } };

            var result = CycleHistoryService.Build(measured, new Dictionary<DateTime, double>(), perBattery, [first, second], Day.AddMinutes(15));

            // 00:00: only battery 1 active, bank 2 kWh on 4 kWh = 0.5. 00:15: bank 4 kWh on 8 kWh = 0.5.
            Assert.Equal(1.0, result.Total.TotalCycles(true), 6);

            var one = result.Batteries[0].Months.Single();
            Assert.Equal(0.25, one.MeasuredCycles, 6);           // own reading 1 kWh / 4 kWh
            Assert.Equal(0.5, one.EstimatedCycles, 6);           // 00:00 follows the bank

            var two = result.Batteries[1].Months.Single();
            Assert.Equal(0.0, two.MeasuredCycles, 6);
            Assert.Equal(0.5, two.EstimatedCycles, 6);           // only active from 00:15
        }

        private static List<CycleMonth> MonthsOf(DateTime first, int count, double cyclesPerMonth) =>
            Enumerable.Range(0, count).Select(i => new CycleMonth(first.AddMonths(i), cyclesPerMonth, 0.0, 0.0, 0.0, 0.0)).ToList();

        [Fact]
        public void Lifetime_uses_the_last_full_year_and_the_earlier_end()
        {
            // 24 months of 20 cycles: 480 used, pace 240/yr, 6000 rated → 23 years; 15 years of age comes first.
            var start = new DateTime(2024, 1, 1);
            var now = new DateTime(2026, 1, 15);
            var series = new CycleSeries("Sessy 1", 5.0, start, MonthsOf(start, 24, 20.0), RatedCycles: 6000, LifetimeYears: 15);

            var life = series.Lifetime(withEstimate: true, now);

            Assert.True(life.PaceOverFullYear);
            Assert.Equal(240.0, life.CyclesPerYear, 6);
            Assert.Equal(8.0, life.UsedPercent!.Value, 6);
            Assert.Equal(new DateTime(2039, 1, 1), life.CalendarEnd);
            Assert.Equal(2049, life.CyclesEnd!.Value.Year);
            Assert.Equal(life.CalendarEnd, life.End);
            Assert.Equal(LifeLimit.Age, life.LimitedBy);
        }

        [Fact]
        public void Lifetime_with_short_history_averages_since_the_start()
        {
            // 100 cycles in half a year → 200/yr; 900 rated → 4 years left, before the 25 years.
            var start = new DateTime(2026, 1, 1);
            var now = start.AddDays(182.625);
            var series = new CycleSeries("Sessy 1", 5.0, start, MonthsOf(start, 6, 100.0 / 6.0), RatedCycles: 900, LifetimeYears: 25);

            var life = series.Lifetime(withEstimate: true, now);

            Assert.False(life.PaceOverFullYear);
            Assert.Equal(200.0, life.CyclesPerYear, 6);
            Assert.Equal(LifeLimit.Cycles, life.LimitedBy);
            Assert.Equal(2030, life.End!.Value.Year);
        }

        [Fact]
        public void Lifetime_without_ratings_has_no_end()
        {
            var start = new DateTime(2026, 1, 1);
            var life = new CycleSeries("Total", 5.0, start, MonthsOf(start, 3, 10.0)).Lifetime(true, start.AddMonths(3));

            Assert.Null(life.End);
            Assert.Null(life.UsedPercent);
        }
    }
}
