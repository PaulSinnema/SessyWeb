using SessyController.Services.Statistics;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    public class CycleHistoryServiceTests
    {
        private static readonly DateTime Day = new(2026, 1, 1);

        [Fact]
        public void QuarterGridDeltas_skips_intervals_longer_than_a_quarter()
        {
            var histories = new[]
            {
                new EnergyHistory { Time = Day, MeterId = "P1", ConsumedTariff1 = 1000 },
                new EnergyHistory { Time = Day.AddMinutes(15), MeterId = "P1", ConsumedTariff1 = 1200 },
                new EnergyHistory { Time = Day.AddHours(5), MeterId = "P1", ConsumedTariff1 = 9000 }
            };

            var deltas = CycleHistoryService.QuarterGridDeltas(histories);

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
    }
}
