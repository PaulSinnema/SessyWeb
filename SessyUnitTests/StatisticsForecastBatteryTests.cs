using SessyController.Services.Statistics;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    public class StatisticsForecastBatteryTests
    {
        private static IEnumerable<ForecastSnapshot> Day(DateTime date, double watts, int quarters = 96) =>
            Enumerable.Range(0, quarters).Select(q => new ForecastSnapshot { Time = date.AddMinutes(15 * q), LeadHours = 24, ConsumptionForecastW = watts });

        [Fact]
        public void Compare_turns_quarter_watts_into_daily_kWh_and_skips_incomplete_days()
        {
            var monday = new DateTime(2026, 11, 9);
            var snapshots = Day(monday, 500.0).Concat(Day(monday.AddDays(1), 500.0, quarters: 40)); // 500 W all day = 12 kWh
            var days = new[] { new WeatherDay(monday, 10.0, 8.0, 0, 0), new WeatherDay(monday.AddDays(1), 10.0, 8.0, 0, 0) };

            var result = ForecastAccuracyService.Compare(days, snapshots, null, 24);

            var day = Assert.Single(result.Days);
            Assert.Equal(12.0, day.PlannerKWh, 6);
            Assert.Equal(20.0, result.Planner!.MeanAbsolutePercent, 6); // |12 − 10| / 10
            Assert.Equal(20.0, result.Planner.BiasPercent, 6);
            Assert.Null(result.Model);
        }

        [Fact]
        public void Compare_adds_the_model_prediction()
        {
            var monday = new DateTime(2026, 11, 9);
            var model = new ConsumptionModel(10.0, 1.0, 0.0, 0.0, 13, 26, monday.AddYears(-1), monday, 365, 0.9, 0.5, 5.0);
            var days = new[] { new WeatherDay(monday, 14.0, 8.0, 0, 0) };

            var result = ForecastAccuracyService.Compare(days, Day(monday, 500.0), model, 24);

            Assert.Equal(15.0, result.Days[0].ModelKWh!.Value, 6); // 10 + (13 − 8)
            Assert.Equal(1.0 / 14.0 * 100.0, result.Model!.BiasPercent, 6);
        }

        [Fact]
        public void Build_counts_energy_cycles_and_efficiency_per_month()
        {
            // November: 10 kWh in (4 quarters of 10 kW), 9 kWh out, SOC back where it started; capacity 10 kWh.
            var start = new DateTime(2026, 11, 1);
            var measurements = new List<QuarterlyMeasurement>();
            for (int q = 0; q < 8; q++)
                measurements.Add(new QuarterlyMeasurement { Time = start.AddMinutes(15 * q), BatteryPowerWatts = q < 4 ? -10000 : 9000, BatteryStateOfChargeWh = 5000 });
            measurements.Add(new QuarterlyMeasurement { Time = start.AddMonths(1), BatteryPowerWatts = 0, BatteryStateOfChargeWh = 5000 });

            // Below 20 kWh charged the efficiency is not trusted: scale up to 30 kWh in, 27 kWh out.
            foreach (var m in measurements) m.BatteryPowerWatts *= 3;

            var result = BatteryStatisticsService.Build(measurements, 10.0, 6000);

            Assert.Equal(2, result.Months.Count);
            var november = result.Months[0];
            Assert.Equal(30.0, november.ChargedKWh, 6);
            Assert.Equal(27.0, november.DischargedKWh, 6);
            Assert.Equal(90.0, november.RoundTripPercent!.Value, 6);
            Assert.Equal(2.7, november.Cycles, 6);
            Assert.Equal(2.7, result.TotalCycles, 6);
        }

        [Fact]
        public void ExpectedCycles_is_capacity_weighted()
        {
            var batteries = new List<Investment>
            {
                new() { CapacityWh = 10000, ExpectedTotalCycles = 6000 },
                new() { CapacityWh = 5000, ExpectedTotalCycles = 3000 },
                new() { CapacityWh = 5000, ExpectedTotalCycles = 0 }
            };

            Assert.Equal(5000, BatteryStatisticsService.ExpectedCycles(batteries));
        }
    }
}
