using SessyController.Services.Statistics;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    public class BatteryHealthTests
    {
        private const double NominalWh = 5000.0;

        /// <summary>An idle quarter, four quarters at 2 kW (2 kWh) and an idle quarter, on a battery of the given capacity.</summary>
        private static IEnumerable<BatteryQuarterMeasurement> Run(DateTime start, double capacityKWh)
        {
            var soc = 4500.0;
            yield return Row(start, 0.0, soc);

            for (int q = 1; q <= 4; q++)
            {
                soc -= 0.5 / capacityKWh * NominalWh;
                yield return Row(start.AddMinutes(15 * q), 2000.0, soc);
            }

            yield return Row(start.AddMinutes(75), 0.0, soc);
        }

        private static BatteryQuarterMeasurement Row(DateTime time, double watts, double socWh) =>
            new() { Time = time, BatteryId = "1", PowerWatts = watts, StateOfChargeWh = socWh, CapacityWh = NominalWh };

        [Fact]
        public void Runs_divide_the_energy_by_the_state_of_charge_it_took()
        {
            var run = Assert.Single(BatteryHealth.Runs(Run(new DateTime(2026, 1, 1, 18, 0, 0), 4.0)));

            Assert.Equal(4.0, run.CapacityKWh, 6);
        }

        [Fact]
        public void Runs_skip_short_discharges()
        {
            // 2 kWh from a 10 kWh battery is 20 % of the state of charge: too little to judge.
            Assert.Empty(BatteryHealth.Runs(Run(new DateTime(2026, 1, 1, 18, 0, 0), 10.0)));
        }

        [Fact]
        public void Estimate_finds_the_fade_and_when_it_reaches_the_threshold()
        {
            // Twelve months, three runs each, 4 kWh losing 5 % of it per year.
            var first = new DateTime(2025, 1, 1);
            var rows = new List<BatteryQuarterMeasurement>();

            for (int m = 0; m < 12; m++)
            {
                foreach (var day in new[] { 1, 10, 20 })
                {
                    var t = first.AddMonths(m).AddDays(day - 1).AddHours(18);
                    var years = (t - first).TotalDays / 365.25;
                    rows.AddRange(Run(t, 4.0 * (1.0 - 0.05 * years)));
                }
            }

            var health = BatteryHealth.Estimate(rows, new DateTime(2026, 1, 1));

            Assert.NotNull(health);
            Assert.Equal(12, health!.Months.Count);
            Assert.InRange(health.FadePercentPerYear!.Value, -5.5, -4.5);
            Assert.InRange(health.CurrentPercent!.Value, 93.0, 97.0);
            Assert.InRange(health.EndOfLife!.Value.Year, 2030, 2032);
        }

        [Fact]
        public void Estimate_needs_half_a_year_for_a_trend()
        {
            var first = new DateTime(2026, 1, 1);
            var rows = Enumerable.Range(0, 3)
                .SelectMany(m => new[] { 1, 10, 20 }.SelectMany(d => Run(first.AddMonths(m).AddDays(d - 1).AddHours(18), 4.0)))
                .ToList();

            var health = BatteryHealth.Estimate(rows, first.AddMonths(3));

            Assert.Equal(100.0, health!.CurrentPercent!.Value, 6);
            Assert.Null(health.FadePercentPerYear);
            Assert.Null(health.EndOfLife);
        }
    }
}
