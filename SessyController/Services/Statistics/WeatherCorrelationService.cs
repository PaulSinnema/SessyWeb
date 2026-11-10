using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services.Statistics
{
    /// <summary>Meteorological season of a day (Dec–Feb winter, Mar–May spring, …).</summary>
    public enum Season { Winter, Spring, Summer, Autumn }

    /// <summary>One complete day: house consumption and the day's average weather.</summary>
    public sealed record WeatherDay(DateTime Date, double ConsumptionKWh, double TemperatureC, double GlobalRadiation, double Humidity)
    {
        public Season Season => SeasonOf(Date);

        public static Season SeasonOf(DateTime date) => date.Month switch
        {
            12 or 1 or 2 => Season.Winter,
            3 or 4 or 5 => Season.Spring,
            6 or 7 or 8 => Season.Summer,
            _ => Season.Autumn
        };
    }

    /// <summary>Least-squares line y = Intercept + Slope·x with Pearson r over N points.</summary>
    public sealed record LinearFit(double Slope, double Intercept, double R, int N)
    {
        public double RSquared => R * R;

        public double At(double x) => Intercept + Slope * x;
    }

    /// <summary>
    /// Daily house consumption against the day's average temperature, global radiation and humidity,
    /// for the Statistics page. One point per day: per quarter the daily rhythm (cooking, night)
    /// drowns the weather. Only complete days count, so a partly recorded day does not read as a
    /// low-consumption day.
    /// </summary>
    public class WeatherCorrelationService
    {
        /// <summary>Quarters a day needs to count as complete (96, minus a few missed readings).</summary>
        public const int MinQuartersPerDay = 92;

        private readonly ConsumptionDataService _consumptionDataService;

        public WeatherCorrelationService(ConsumptionDataService consumptionDataService)
        {
            _consumptionDataService = consumptionDataService;
        }

        /// <summary>All complete days with consumption data, oldest first.</summary>
        public async Task<List<WeatherDay>> GetDaysAsync()
        {
            var quarters = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            return ToDays(quarters);
        }

        /// <summary>Groups quarters into complete days. Pure, so it can be tested on its own.</summary>
        public static List<WeatherDay> ToDays(IEnumerable<Consumption> quarters) =>
            quarters
                .GroupBy(q => q.Time.Date)
                .Where(g => g.Count() >= MinQuartersPerDay)
                .Select(g => new WeatherDay(
                    g.Key,
                    // ConsumptionWh is the average power (W) over the quarter: W × 0.25 h = Wh.
                    g.Sum(q => q.ConsumptionWh) * 0.25 / 1000.0,
                    g.Average(q => q.Temperature),
                    g.Average(q => q.GlobalRadiation),
                    g.Average(q => q.Humidity)))
                .OrderBy(d => d.Date)
                .ToList();

        /// <summary>Least-squares fit; null with fewer than 3 points or no spread in x.</summary>
        public static LinearFit? Fit(IReadOnlyList<(double X, double Y)> points)
        {
            int n = points.Count;
            if (n < 3) return null;

            double meanX = points.Average(p => p.X);
            double meanY = points.Average(p => p.Y);

            double sxx = 0.0, syy = 0.0, sxy = 0.0;
            foreach (var (x, y) in points)
            {
                sxx += (x - meanX) * (x - meanX);
                syy += (y - meanY) * (y - meanY);
                sxy += (x - meanX) * (y - meanY);
            }

            if (sxx <= 0.0) return null;

            double slope = sxy / sxx;
            double r = syy <= 0.0 ? 0.0 : sxy / Math.Sqrt(sxx * syy);

            return new LinearFit(slope, meanY - slope * meanX, r, n);
        }
    }
}
