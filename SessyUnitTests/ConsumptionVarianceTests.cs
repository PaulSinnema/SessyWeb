using SessyController.Services.Statistics;
using Xunit;

namespace SessyTests.Services
{
    public class ConsumptionVarianceTests
    {
        // 10 kWh base, 1 kWh per degree below 13 °C, no cooling, no weekend effect; RMSE 0.5.
        private static readonly ConsumptionModel Model = new(10.0, 1.0, 0.0, 0.0, 13, 26,
            new DateTime(2025, 1, 1), new DateTime(2026, 1, 1), 365, 0.9, 0.5, 5.0);

        private static WeatherDay Day(DateTime date, double t, double extra = 0.0) =>
            new(date, Model.Predict(date, t) + extra, t, 0, 0);

        private static Dictionary<DateTime, WeatherDay> Days(params WeatherDay[] days) => days.ToDictionary(d => d.Date);

        [Fact]
        public void PairedItem_splits_one_day_against_one_day()
        {
            var current = new DateTime(2026, 11, 9);
            var previous = current.AddDays(-364);
            var days = Days(Day(previous, 10.0), Day(current, 2.0, 3.0));

            var item = ConsumptionVariance.PairedItem(Model, days, "Mo 9", previous, current);

            Assert.True(item.HasData);
            Assert.Equal(8.0, item.Difference.TemperatureKWh, 6);
            Assert.Equal(3.0, item.Difference.OtherKWh, 6);
        }

        [Fact]
        public void PairedItem_without_both_complete_days_has_no_data()
        {
            var current = new DateTime(2026, 11, 9);
            var days = Days(Day(current, 2.0));

            var item = ConsumptionVariance.PairedItem(Model, days, "Mo 9", current.AddDays(-364), current);

            Assert.False(item.HasData);
            Assert.Equal(0.0, item.Difference.CurrentKWh);
        }

        [Fact]
        public void PairedGroupItem_only_counts_pairs_with_both_days_complete()
        {
            var a = new DateTime(2026, 9, 1);
            var b = new DateTime(2026, 9, 2);
            // b has no previous-year day: that pair is skipped, so both sides count one day.
            var days = Days(Day(a.AddYears(-1), 10.0), Day(a, 10.0, 2.0), Day(b, 10.0));

            var item = ConsumptionVariance.PairedGroupItem(Model, days, "2026/9", new[] { (a.AddYears(-1), a), (b.AddYears(-1), b) });

            Assert.Equal(1, item.PreviousDays);
            Assert.Equal(1, item.CurrentDays);
            Assert.Equal(2.0, item.Difference.OtherKWh, 6);
        }

        [Fact]
        public void Combine_totals_only_items_with_data_and_sets_the_noise_band()
        {
            var a = new DateTime(2026, 11, 9);
            var b = new DateTime(2026, 11, 10);
            var days = Days(Day(a.AddDays(-364), 10.0), Day(a, 2.0, 1.0), Day(b.AddDays(-364), 10.0), Day(b, 4.0, -2.0));
            var items = new List<VarianceItem>
            {
                ConsumptionVariance.PairedItem(Model, days, "a", a.AddDays(-364), a),
                ConsumptionVariance.PairedItem(Model, days, "b", b.AddDays(-364), b),
                ConsumptionVariance.PairedItem(Model, days, "c", b.AddDays(-363), b.AddDays(1))   // no data
            };

            var result = ConsumptionVariance.Combine(Model, items);

            Assert.Equal(8.0 + 6.0, result.Total.TemperatureKWh, 6);
            Assert.Equal(1.0 - 2.0, result.Total.OtherKWh, 6);
            Assert.Equal(2, result.CurrentDays);
            Assert.Equal(2, result.PreviousDays);
            Assert.Equal(2.0 * 0.5 * Math.Sqrt(4), result.NormalVariationKWh, 6);
            Assert.Equal(a, result.From);
            Assert.Equal(b, result.To);
        }

        [Fact]
        public void ModelFitPerMonth_accumulates_the_difference()
        {
            var days = new[]
            {
                Day(new DateTime(2026, 1, 5), 5.0, 2.0),
                Day(new DateTime(2026, 1, 6), 5.0, 1.0),
                Day(new DateTime(2026, 2, 2), 5.0, -4.0)
            };

            var fit = ConsumptionVariance.ModelFitPerMonth(Model, days);

            Assert.Equal(2, fit.Count);
            Assert.Equal("2026-01", fit[0].Label);
            Assert.Equal(3.0, fit[0].CumulativeDeviationKWh, 6);
            Assert.Equal(-1.0, fit[1].CumulativeDeviationKWh, 6);
            Assert.Equal(2, fit[0].Days);
        }
    }
}
