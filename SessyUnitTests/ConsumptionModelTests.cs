using SessyController.Services.Statistics;
using Xunit;

namespace SessyTests.Services
{
    public class ConsumptionModelTests
    {
        // Known house: 10 kWh base, 1.0 per degree below 13 °C, 2.0 per degree above 21 °C, −1.5 on weekend days.
        private static double Truth(DateTime date, double t) =>
            10.0 + 1.0 * Math.Max(0.0, 13.0 - t) + 2.0 * Math.Max(0.0, t - 21.0) - (ConsumptionModel.IsWeekend(date) ? 1.5 : 0.0);

        // A year of days with a seasonal temperature (−2..26 °C) plus a weekly wobble, so weekends are not tied to temperature.
        private static List<WeatherDay> Year(DateTime start, int count = 365, Func<DateTime, double, double>? consumption = null) =>
            Enumerable.Range(0, count).Select(i =>
            {
                var date = start.AddDays(i);
                var t = 12.0 - 14.0 * Math.Cos(2 * Math.PI * i / 365.0) + 1.5 * Math.Sin(i * 0.9);
                return new WeatherDay(date, (consumption ?? Truth)(date, t), t, 0, 0);
            }).ToList();

        [Fact]
        public void Fit_recovers_base_temperatures_and_coefficients()
        {
            var model = ConsumptionModel.Fit(Year(new DateTime(2025, 1, 1)))!;

            Assert.Equal(13, model.HeatingBaseC);
            Assert.Equal(21, model.CoolingBaseC);
            Assert.Equal(10.0, model.Base, 6);
            Assert.Equal(1.0, model.PerHeatingDegree, 6);
            Assert.Equal(2.0, model.PerCoolingDegree, 6);
            Assert.Equal(-1.5, model.WeekendEffect, 6);
            Assert.Equal(1.0, model.RSquared, 6);
            Assert.Equal(0.0, model.RmseKWh, 6);
            Assert.Equal(0.0, model.WeeklyErrorPercent, 6);
        }

        [Fact]
        public void Only_the_reference_period_is_used()
        {
            var start = new DateTime(2025, 1, 1);
            var days = Year(start)
                .Concat(Year(start.AddDays(365), 100, (date, t) => Truth(date, t) + 50.0))   // a later, very different period
                .ToList();

            var model = ConsumptionModel.Fit(days)!;

            Assert.Equal(start, model.ReferenceStart);
            Assert.Equal(start.AddDays(365), model.ReferenceEnd);
            Assert.Equal(365, model.Days);
            Assert.Equal(10.0, model.Base, 6);
        }

        [Fact]
        public void A_chosen_reference_period_is_used()
        {
            var start = new DateTime(2025, 1, 1);
            var days = Year(start, 365, (date, t) => Truth(date, t) + 5.0).Concat(Year(start.AddDays(365))).ToList();

            var model = ConsumptionModel.Fit(days, start.AddDays(365), start.AddDays(730))!;

            Assert.Equal(10.0, model.Base, 6);
        }

        [Fact]
        public void Too_few_days_give_no_model()
        {
            Assert.Null(ConsumptionModel.Fit(new List<WeatherDay>()));
            Assert.Null(ConsumptionModel.Fit(Year(new DateTime(2025, 1, 1), ConsumptionModel.MinReferenceDays - 1)));
        }

        [Fact]
        public void Without_warm_days_the_cooling_term_is_left_out()
        {
            // Winter only: never above 18 °C.
            var days = Enumerable.Range(0, 120).Select(i =>
            {
                var date = new DateTime(2025, 11, 1).AddDays(i);
                var t = 5.0 + 6.0 * Math.Sin(i * 0.3);
                return new WeatherDay(date, Truth(date, t), t, 0, 0);
            }).ToList();

            var model = ConsumptionModel.Fit(days)!;

            Assert.Equal(0.0, model.PerCoolingDegree, 6);
            Assert.Equal(1.0, model.PerHeatingDegree, 6);
        }

        [Fact]
        public void Compare_splits_the_difference_into_temperature_calendar_and_other()
        {
            var model = ConsumptionModel.Fit(Year(new DateTime(2025, 1, 1)))!;

            // Same weekdays, last year 10 °C, this year 2 °C and 3 kWh/day more for other reasons.
            var previous = Enumerable.Range(0, 7).Select(i => new DateTime(2025, 11, 10).AddDays(i))
                .Select(d => new WeatherDay(d, Truth(d, 10.0), 10.0, 0, 0)).ToList();
            var current = Enumerable.Range(0, 7).Select(i => new DateTime(2026, 11, 9).AddDays(i))
                .Select(d => new WeatherDay(d, Truth(d, 2.0) + 3.0, 2.0, 0, 0)).ToList();

            var difference = model.Compare(previous, current);

            Assert.Equal(7 * 8.0, difference.TemperatureKWh, 6);   // 8 degrees colder × 1 kWh × 7 days
            Assert.Equal(0.0, difference.CalendarKWh, 6);
            Assert.Equal(7 * 3.0, difference.OtherKWh, 6);
            Assert.Equal(difference.CurrentKWh,
                difference.PreviousKWh + difference.TemperatureKWh + difference.CalendarKWh + difference.OtherKWh, 6);
        }

        [Fact]
        public void Predict_includes_the_weekend_effect()
        {
            var model = ConsumptionModel.Fit(Year(new DateTime(2025, 1, 1)))!;

            Assert.Equal(13.0, model.Predict(new DateTime(2026, 11, 13), 10.0), 6);   // Friday: 10 + 3
            Assert.Equal(11.5, model.Predict(new DateTime(2026, 11, 14), 10.0), 6);   // Saturday: 10 + 3 − 1.5
        }

        [Fact]
        public void LeastSquares_rejects_singular_systems()
        {
            var rows = new List<double[]> { new[] { 1.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 0.0 } };

            Assert.Null(ConsumptionModel.LeastSquares(rows, new List<double> { 1, 2, 3 }));
        }
    }
}
