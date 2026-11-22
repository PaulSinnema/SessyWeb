using SessyController.Services.Items;
using SessyController.Services.Statistics;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    public class ConsumptionPatternPriceCaptureTests
    {
        // A night of 24 quarters: four quiet ones at the given load, the rest at 400 W.
        private static IEnumerable<Consumption> Night(DateTime date, double quietW) =>
            Enumerable.Range(0, 24).Select(q => new Consumption { Time = date.AddMinutes(15 * q), ConsumptionWh = q < 4 ? quietW : 400.0 });

        [Fact]
        public void BaseLoad_takes_the_quietest_hour_and_the_weekly_median()
        {
            var monday = new DateTime(2026, 11, 2);
            var quarters = Night(monday, 100).Concat(Night(monday.AddDays(1), 120)).Concat(Night(monday.AddDays(2), 300));

            var result = ConsumptionPatternService.BaseLoad(quarters);

            var week = Assert.Single(result.Weeks);
            Assert.Equal(monday, week.WeekStart);
            Assert.Equal(120.0, week.MedianW, 6);
            Assert.Equal(3, week.Nights);
            Assert.Null(result.YearAgoW);
        }

        [Fact]
        public void BaseLoad_skips_weeks_with_too_few_nights()
        {
            var result = ConsumptionPatternService.BaseLoad(Night(new DateTime(2026, 11, 2), 100));

            Assert.Empty(result.Weeks);
            Assert.Equal(100.0, result.RecentW!.Value, 6);
        }

        [Fact]
        public void Heatmap_averages_per_weekday_and_hour()
        {
            var monday = new DateTime(2026, 11, 2, 18, 0, 0);
            var quarters = new[]
            {
                new Consumption { Time = monday, ConsumptionWh = 1000 },
                new Consumption { Time = monday.AddMinutes(15), ConsumptionWh = 3000 },
                new Consumption { Time = monday.AddDays(7), ConsumptionWh = 2000 }
            };

            var result = ConsumptionPatternService.Heatmap(quarters, monday.Date, monday.Date.AddDays(14));

            var cell = Assert.Single(result.Cells);
            Assert.Equal(DayOfWeek.Monday, cell.Day);
            Assert.Equal(18, cell.Hour);
            Assert.Equal(2000.0, cell.AverageW, 6);
            Assert.Equal(3, cell.Quarters);
        }

        [Fact]
        public void Median_handles_even_and_odd_counts()
        {
            Assert.Equal(2.0, Stats.Median([3, 1, 2]));
            Assert.Equal(2.5, Stats.Median([4, 1, 2, 3]));
        }

        [Fact]
        public void PriceCapture_compares_achieved_with_best_spread()
        {
            // Prices 0.10 .. 0.40; charged at 0.20, discharged at 0.30, 1 kWh each (4 kW for a quarter).
            var day = new DateTime(2026, 11, 2);
            var quarters = new List<MeasurementView>
            {
                new() { Time = day, BuyingPriceEur = 0.10 },
                new() { Time = day.AddHours(1), BuyingPriceEur = 0.20, BatteryPowerWatts = -4000 },
                new() { Time = day.AddHours(2), BuyingPriceEur = 0.30, BatteryPowerWatts = 4000 },
                new() { Time = day.AddHours(3), BuyingPriceEur = 0.40 }
            };

            var result = PriceCaptureService.Day(day, quarters)!;

            Assert.Equal(0.10, result.CapturedEurPerKWh, 6);
            Assert.Equal(0.30, result.BestEurPerKWh, 6);   // 0.40 − 0.10
            Assert.Equal(1.0, result.DischargedKWh, 6);

            var month = Assert.Single(PriceCaptureService.Months([result]));
            Assert.Equal(100.0 / 3.0, month.CapturePercent!.Value, 6);
        }

        [Fact]
        public void PriceCapture_best_is_an_upper_bound_with_small_discharge_quarters()
        {
            // 1 kWh charged at 0.10; 1 kWh discharged at 0.40 plus two small house-cover quarters (0.04 kWh each).
            var day = new DateTime(2026, 11, 2);
            var quarters = new List<MeasurementView>
            {
                new() { Time = day, BuyingPriceEur = 0.10, BatteryPowerWatts = -4000 },
                new() { Time = day.AddHours(1), BuyingPriceEur = 0.12 },
                new() { Time = day.AddHours(2), BuyingPriceEur = 0.15, BatteryPowerWatts = 160 },
                new() { Time = day.AddHours(3), BuyingPriceEur = 0.20, BatteryPowerWatts = 160 },
                new() { Time = day.AddHours(4), BuyingPriceEur = 0.40, BatteryPowerWatts = 4000 }
            };

            var result = PriceCaptureService.Day(day, quarters)!;

            Assert.Equal(0.414 / 1.08 - 0.10, result.CapturedEurPerKWh, 6);
            Assert.Equal(0.416 / 1.08 - 0.10, result.BestEurPerKWh, 6);   // 1 kWh at 0.40, 0.08 kWh at 0.20
            Assert.True(result.CapturedEurPerKWh <= result.BestEurPerKWh);
        }

        [Fact]
        public void PriceCapture_skips_days_without_trading()
        {
            var day = new DateTime(2026, 11, 2);
            var quarters = new List<MeasurementView> { new() { Time = day, BuyingPriceEur = 0.20, BatteryPowerWatts = 4000 } };

            Assert.Null(PriceCaptureService.Day(day, quarters));
        }
    }
}
