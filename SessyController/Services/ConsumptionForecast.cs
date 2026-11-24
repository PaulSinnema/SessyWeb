using System.Globalization;
using SessyController.Services.Statistics;
using SessyData.Model;
using static SessyController.Services.WeatherService;

namespace SessyController.Services
{
    /// <summary>
    /// Consumption forecast for quarters beyond the hourly weather forecast (about 24 hours): the day total from the
    /// weather model and the daily forecast temperature, spread over the day by the usual profile of that weekday.
    /// Pure, so it can be tested on its own.
    /// </summary>
    public sealed class ConsumptionForecast
    {
        /// <summary>Average power (W) per weekday and hour.</summary>
        private readonly Dictionary<(DayOfWeek Day, int Hour), double> _profile;

        /// <summary>Energy (Wh) of the profile per weekday, to scale it to a day total.</summary>
        private readonly Dictionary<DayOfWeek, double> _profileDayWh;

        private readonly ConsumptionModel? _model;

        /// <summary>Forecast average temperature per date.</summary>
        private readonly Dictionary<DateTime, double> _dayTemperatures;

        public ConsumptionForecast(IReadOnlyCollection<Consumption> history, ConsumptionModel? model, IReadOnlyDictionary<DateTime, double> dayTemperatures)
        {
            _profile = history
                .Where(c => c.ConsumptionWh > 0.0)
                .GroupBy(c => (c.Time.DayOfWeek, c.Time.Hour))
                .ToDictionary(g => g.Key, g => g.Average(c => c.ConsumptionWh)); // ConsumptionWh holds average watts

            // A weekday counts only with all 24 hours, otherwise its shape is incomplete.
            _profileDayWh = _profile
                .GroupBy(kv => kv.Key.Day)
                .Where(g => g.Count() == 24)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

            _model = model;
            _dayTemperatures = new Dictionary<DateTime, double>(dayTemperatures);
        }

        public bool HasModel => _model != null;

        /// <summary>Expected kWh of a day from the model and its forecast temperature; null when either is missing.</summary>
        public double? DayKWh(DateTime date) =>
            _model != null && _dayTemperatures.TryGetValue(date.Date, out var temperature)
                ? Math.Max(0.0, _model.Predict(date.Date, temperature))
                : null;

        /// <summary>Average watts of a quarter, from a day total, shaped by the weekday profile (flat without one).</summary>
        public double QuarterWatts(DateTime time, double dayKWh)
        {
            var dayWh = dayKWh * 1000.0;

            if (_profileDayWh.TryGetValue(time.DayOfWeek, out var profileWh) && profileWh > 0.0
                && _profile.TryGetValue((time.DayOfWeek, time.Hour), out var hourW))
                return hourW * dayWh / profileWh;

            return dayWh / 24.0;
        }

        /// <summary>
        /// Average temperature per date from the daily forecast (mean of min and max). Dates Weerlive sends in
        /// another format than dd-MM-yyyy are skipped.
        /// </summary>
        public static Dictionary<DateTime, double> DayTemperatures(IEnumerable<DagVerwachting>? days)
        {
            var result = new Dictionary<DateTime, double>();

            foreach (var day in days ?? Enumerable.Empty<DagVerwachting>())
            {
                if (DateTime.TryParseExact(day.Dag, new[] { "dd-MM-yyyy", "d-M-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    result[date.Date] = (day.MinTemp + day.MaxTemp) / 2.0;
            }

            return result;
        }
    }
}
