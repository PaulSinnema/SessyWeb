using SessyData.Model;
using SessyData.Services;
using SessyController.Services.Items;

namespace SessyController.Services.Statistics
{
    /// <summary>Median night-time base load of one week (Monday start).</summary>
    public sealed record BaseLoadWeek(DateTime WeekStart, double MedianW, int Nights)
    {
        public string Label => WeekStart.ToString("dd-MM-yy");
    }

    public sealed record BaseLoadResult(List<BaseLoadWeek> Weeks, double? RecentW, double? YearAgoW, double? FirstW);

    /// <summary>Average load (W) for one weekday and hour.</summary>
    public sealed record HeatmapCell(DayOfWeek Day, int Hour, double AverageW, int Quarters);

    public sealed record HeatmapResult(List<HeatmapCell> Cells, DateTime From, DateTime To, double MinW, double MaxW);

    /// <summary>Patterns in the measured household load: night-time base load over time and load per weekday and hour.</summary>
    public class ConsumptionPatternService
    {
        /// <summary>Night window used for the base load: 00:00 – 06:00.</summary>
        public const int NightEndHour = 6;

        /// <summary>Lowest quarters of a night that are averaged: the quietest hour.</summary>
        private const int QuietQuarters = 4;

        /// <summary>Fewest night quarters (of 24) for a night to count.</summary>
        private const int MinNightQuarters = 16;

        /// <summary>Fewest nights for a week to count.</summary>
        private const int MinNightsPerWeek = 3;

        /// <summary>Nights compared in the summary (four weeks).</summary>
        private const int SummaryNights = 28;

        private readonly ConsumptionDataService _consumptionDataService;

        public ConsumptionPatternService(ConsumptionDataService consumptionDataService)
        {
            _consumptionDataService = consumptionDataService;
        }

        public async Task<BaseLoadResult> GetBaseLoadAsync()
        {
            var quarters = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set.Where(c => c.Time.Hour < NightEndHour).ToList()));

            return BaseLoad(quarters);
        }

        public async Task<HeatmapResult> GetHeatmapAsync(DateTime from, DateTime to)
        {
            var quarters = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set.Where(c => c.Time >= from && c.Time < to).ToList()));

            return Heatmap(quarters, from, to);
        }

        /// <summary>Per night the mean of the quietest quarters; per week the median of those nights.</summary>
        public static BaseLoadResult BaseLoad(IEnumerable<Consumption> quarters)
        {
            // Consumption.ConsumptionWh holds the quarter's average power in W.
            var nights = quarters
                .Where(q => q.Time.Hour < NightEndHour && q.ConsumptionWh > 0.0)
                .GroupBy(q => q.Time.Date)
                .Where(g => g.Count() >= MinNightQuarters)
                .Select(g => (Date: g.Key, W: g.Select(q => q.ConsumptionWh).OrderBy(w => w).Take(QuietQuarters).Average()))
                .OrderBy(n => n.Date)
                .ToList();

            var weeks = nights
                .GroupBy(n => n.Date.AddDays(-(((int)n.Date.DayOfWeek + 6) % 7)))
                .Where(g => g.Count() >= MinNightsPerWeek)
                .Select(g => new BaseLoadWeek(g.Key, Stats.Median(g.Select(n => n.W)), g.Count()))
                .OrderBy(w => w.WeekStart)
                .ToList();

            if (nights.Count == 0)
                return new BaseLoadResult(weeks, null, null, null);

            var last = nights[^1].Date;
            double? Window(DateTime end)
            {
                var window = nights.Where(n => n.Date > end.AddDays(-SummaryNights) && n.Date <= end).Select(n => n.W).ToList();
                return window.Count > 0 ? Stats.Median(window) : null;
            }

            var yearAgo = last.AddYears(-1);

            return new BaseLoadResult(weeks,
                Window(last),
                nights[0].Date <= yearAgo.AddDays(-SummaryNights + 1) ? Window(yearAgo) : null,
                Stats.Median(nights.Take(SummaryNights).Select(n => n.W)));
        }

        /// <summary>Average load per weekday and hour over the given quarters.</summary>
        public static HeatmapResult Heatmap(IEnumerable<Consumption> quarters, DateTime from, DateTime to)
        {
            var cells = quarters
                .Where(q => q.ConsumptionWh > 0.0)
                .GroupBy(q => (q.Time.DayOfWeek, q.Time.Hour))
                .Select(g => new HeatmapCell(g.Key.DayOfWeek, g.Key.Hour, g.Average(q => q.ConsumptionWh), g.Count()))
                .ToList();

            return new HeatmapResult(cells, from, to,
                cells.Count > 0 ? cells.Min(c => c.AverageW) : 0.0,
                cells.Count > 0 ? cells.Max(c => c.AverageW) : 0.0);
        }

    }
}
