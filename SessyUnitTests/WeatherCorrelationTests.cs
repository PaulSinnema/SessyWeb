using SessyController.Services.Statistics;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    public class WeatherCorrelationTests
    {
        private static IEnumerable<Consumption> Day(DateTime date, int quarters, double watts, double tempC) =>
            Enumerable.Range(0, quarters).Select(i => new Consumption
            {
                Time = date.AddMinutes(15 * i),
                ConsumptionWh = watts,
                Temperature = tempC,
                GlobalRadiation = 100,
                Humidity = 80
            });

        [Fact]
        public void A_complete_day_sums_quarter_power_to_kWh()
        {
            // 96 quarters at 1000 W = 24 h × 1 kW = 24 kWh.
            var day = Assert.Single(WeatherCorrelationService.ToDays(Day(new DateTime(2026, 1, 5), 96, 1000, 4)));

            Assert.Equal(24.0, day.ConsumptionKWh, 6);
            Assert.Equal(4.0, day.TemperatureC, 6);
            Assert.Equal(Season.Winter, day.Season);
        }

        [Fact]
        public void An_incomplete_day_is_left_out()
        {
            var quarters = Day(new DateTime(2026, 1, 5), 96, 1000, 4)
                .Concat(Day(new DateTime(2026, 1, 6), WeatherCorrelationService.MinQuartersPerDay - 1, 1000, 4));

            Assert.Single(WeatherCorrelationService.ToDays(quarters));
        }

        [Fact]
        public void Fit_recovers_an_exact_line()
        {
            var points = new List<(double, double)> { (0, 30), (10, 20), (20, 10) };   // y = 30 − x

            var fit = WeatherCorrelationService.Fit(points)!;

            Assert.Equal(-1.0, fit.Slope, 6);
            Assert.Equal(30.0, fit.Intercept, 6);
            Assert.Equal(-1.0, fit.R, 6);
            Assert.Equal(1.0, fit.RSquared, 6);
            Assert.Equal(3, fit.N);
        }

        [Fact]
        public void Fit_needs_three_points_and_spread_in_x()
        {
            Assert.Null(WeatherCorrelationService.Fit(new List<(double, double)> { (1, 2), (2, 3) }));
            Assert.Null(WeatherCorrelationService.Fit(new List<(double, double)> { (5, 1), (5, 2), (5, 3) }));
        }

        [Theory]
        [InlineData(1, Season.Winter)]
        [InlineData(4, Season.Spring)]
        [InlineData(7, Season.Summer)]
        [InlineData(10, Season.Autumn)]
        [InlineData(12, Season.Winter)]
        public void Season_follows_the_meteorological_calendar(int month, Season expected)
        {
            Assert.Equal(expected, WeatherDay.SeasonOf(new DateTime(2026, month, 15)));
        }
    }
}
