using SessyController.Services;
using SessyController.Services.Statistics;
using SessyData.Model;
using Xunit;
using static SessyController.Services.WeatherService;

namespace SessyTests.Services
{
    public class ConsumptionForecastTests
    {
        /// <summary>Two weeks of quarters: 1000 W from 18:00 to 22:00, 250 W otherwise.</summary>
        private static List<Consumption> History()
        {
            var start = new DateTime(2026, 11, 2);
            return Enumerable.Range(0, 14 * 96)
                .Select(i => start.AddMinutes(15 * i))
                .Select(t => new Consumption { Time = t, ConsumptionWh = t.Hour is >= 18 and < 22 ? 1000.0 : 250.0 })
                .ToList();
        }

        private static readonly ConsumptionModel Model = new(Base: 10.0, PerHeatingDegree: 1.0, PerCoolingDegree: 0.0, WeekendEffect: 0.0, HeatingBaseC: 15.0, CoolingBaseC: 30.0,
                ReferenceStart: DateTime.MinValue, ReferenceEnd: DateTime.MinValue, Days: 60, RSquared: 0.8, RmseKWh: 1.0, WeeklyErrorPercent: 5.0);

        [Fact]
        public void Day_total_comes_from_the_model_and_the_daily_temperature()
        {
            var forecast = new ConsumptionForecast(History(), Model, new Dictionary<DateTime, double> { [new DateTime(2026, 11, 25)] = 5.0 });

            Assert.Equal(20.0, forecast.DayKWh(new DateTime(2026, 11, 25, 13, 0, 0))!.Value, 6);   // 10 + 1 × (15 − 5)
            Assert.Null(forecast.DayKWh(new DateTime(2026, 11, 26)));
        }

        [Fact]
        public void Quarters_follow_the_weekday_profile_and_add_up_to_the_day()
        {
            var forecast = new ConsumptionForecast(History(), null, new Dictionary<DateTime, double>());
            var day = new DateTime(2026, 11, 25);

            var quarters = Enumerable.Range(0, 96).Select(i => forecast.QuarterWatts(day.AddMinutes(15 * i), 12.0)).ToList();

            Assert.Equal(12.0, quarters.Sum() * 0.25 / 1000.0, 6);
            Assert.Equal(4.0, forecast.QuarterWatts(day.AddHours(19), 12.0) / forecast.QuarterWatts(day.AddHours(3), 12.0), 6);
        }

        [Fact]
        public void Without_a_profile_the_day_is_flat()
        {
            var forecast = new ConsumptionForecast(new List<Consumption>(), null, new Dictionary<DateTime, double>());

            Assert.Equal(500.0, forecast.QuarterWatts(new DateTime(2026, 11, 25, 3, 0, 0), 12.0), 6);
        }

        [Fact]
        public void Daily_forecast_dates_are_parsed()
        {
            var temperatures = ConsumptionForecast.DayTemperatures(new List<DagVerwachting>
            {
                new() { Dag = "25-11-2026", MinTemp = 2, MaxTemp = 8 },
                new() { Dag = "zaterdag", MinTemp = 0, MaxTemp = 4 }
            });

            Assert.Equal(5.0, Assert.Single(temperatures).Value, 6);
        }
    }
}
