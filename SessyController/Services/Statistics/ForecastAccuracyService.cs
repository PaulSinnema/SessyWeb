using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services.Statistics
{
    /// <summary>One complete day: measured consumption against the planner's forecast and the consumption model.</summary>
    public sealed record ForecastAccuracyDay(DateTime Date, double MeasuredKWh, double PlannerKWh, double? ModelKWh)
    {
        public string Label => Date.ToString("dd-MM");
    }

    /// <summary>Mean absolute error and bias, both as a percentage of the measured consumption.</summary>
    public sealed record ForecastError(double MeanAbsolutePercent, double BiasPercent);

    public sealed record ForecastAccuracyResult(List<ForecastAccuracyDay> Days, int LeadHours, ForecastError? Planner, ForecastError? Model);

    /// <summary>
    /// How well the planner's consumption forecast (as stored <see cref="ForecastSnapshot"/>s) and the
    /// weather-based <see cref="ConsumptionModel"/> predicted each day's consumption.
    /// </summary>
    public class ForecastAccuracyService
    {
        /// <summary>Lead time compared: the forecast as it stood a day ahead.</summary>
        public const int DefaultLeadHours = 24;

        /// <summary>Fewest quarters for a forecast day to count; a DST day has 92.</summary>
        private const int MinQuartersPerDay = 92;

        private readonly ConsumptionModelService _consumptionModelService;
        private readonly ForecastSnapshotDataService _forecastSnapshotDataService;

        public ForecastAccuracyService(ConsumptionModelService consumptionModelService, ForecastSnapshotDataService forecastSnapshotDataService)
        {
            _consumptionModelService = consumptionModelService;
            _forecastSnapshotDataService = forecastSnapshotDataService;
        }

        public async Task<ForecastAccuracyResult> GetAsync(int leadHours = DefaultLeadHours)
        {
            var (model, days) = await _consumptionModelService.GetModelAsync();

            var snapshots = await _forecastSnapshotDataService.GetList(async set =>
                await Task.FromResult(set.Where(s => s.LeadHours == leadHours).ToList()));

            return Compare(days, snapshots, model, leadHours);
        }

        /// <summary>Pairs measured complete days with complete forecast days; the model is optional.</summary>
        public static ForecastAccuracyResult Compare(IEnumerable<WeatherDay> days, IEnumerable<ForecastSnapshot> snapshots, ConsumptionModel? model, int leadHours)
        {
            // W per quarter to kWh: × 0.25 h / 1000.
            var planner = snapshots
                .GroupBy(s => s.Time.Date)
                .Where(g => g.Count() >= MinQuartersPerDay)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.ConsumptionForecastW) * 0.25 / 1000.0);

            var result = days
                .Where(d => planner.ContainsKey(d.Date))
                .OrderBy(d => d.Date)
                .Select(d => new ForecastAccuracyDay(d.Date, d.ConsumptionKWh, planner[d.Date], model?.Predict(d)))
                .ToList();

            return new ForecastAccuracyResult(result, leadHours,
                Error(result, d => d.PlannerKWh),
                model == null ? null : Error(result, d => d.ModelKWh!.Value));
        }

        private static ForecastError? Error(List<ForecastAccuracyDay> days, Func<ForecastAccuracyDay, double> forecast)
        {
            var measured = days.Sum(d => d.MeasuredKWh);

            if (days.Count == 0 || measured <= 0.0)
                return null;

            return new ForecastError(
                days.Sum(d => Math.Abs(forecast(d) - d.MeasuredKWh)) / measured * 100.0,
                days.Sum(d => forecast(d) - d.MeasuredKWh) / measured * 100.0);
        }
    }
}
