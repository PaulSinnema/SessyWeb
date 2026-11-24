using SessyController.Services.Statistics;
using Xunit;

namespace SessyTests.Services
{
    public class SolarHealthTests
    {
        /// <summary>Hourly radiation and quarterly production over a range, with the panels losing fadePerYear % a year.</summary>
        private static (Dictionary<DateTime, double> Solar, Dictionary<(DateTime, int), double> Radiation) Data(DateTime from, DateTime to, double fadePerYear)
        {
            var solar = new Dictionary<DateTime, double>();
            var radiation = new Dictionary<(DateTime, int), double>();

            for (var day = from; day < to; day = day.AddDays(1))
            {
                var years = (day - from).TotalDays / 365.25;
                var factor = Math.Pow(1.0 - fadePerYear / 100.0, years);

                for (int hour = 8; hour < 18; hour++)
                {
                    radiation[(day, hour)] = 500.0;                         // 0.5 kWh per kWp per hour
                    for (int q = 0; q < 4; q++)
                        solar[day.AddHours(hour).AddMinutes(15 * q)] = 0.5 * 5.0 * 0.8 * factor / 4.0; // 5 kWp, PR 0.8
                }
            }

            return (solar, radiation);
        }

        [Fact]
        public void Estimate_finds_the_year_on_year_fade()
        {
            var (solar, radiation) = Data(new DateTime(2024, 1, 1), new DateTime(2026, 7, 1), fadePerYear: 1.0);

            var health = SolarHealth.Estimate(solar, radiation, kWp: 5.0, now: new DateTime(2026, 7, 15));

            Assert.NotNull(health);
            Assert.InRange(health!.FadePercentPerYear!.Value, -1.1, -0.9);
            Assert.InRange(health.CurrentPercent!.Value, 97.0, 98.0);
            Assert.InRange(health.EndOfLife!.Value.Year, 2045, 2048);
        }

        [Fact]
        public void Estimate_needs_a_year_before_it_can_compare()
        {
            var (solar, radiation) = Data(new DateTime(2026, 1, 1), new DateTime(2026, 7, 1), fadePerYear: 1.0);

            var health = SolarHealth.Estimate(solar, radiation, kWp: 5.0, now: new DateTime(2026, 7, 15));

            Assert.Equal(6, health!.Months.Count);
            Assert.Equal(0, health.ComparedMonths);
            Assert.Null(health.FadePercentPerYear);
        }
    }
}
