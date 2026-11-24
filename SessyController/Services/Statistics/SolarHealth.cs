namespace SessyController.Services.Statistics
{
    /// <summary>Performance ratio of one month: measured production ÷ what the radiation and kWp would give.</summary>
    public sealed record SolarMonth(DateTime Month, double PerformanceRatio);

    /// <summary>
    /// Ageing of the solar panels, from the performance ratio compared month by month with the same month a year
    /// earlier (that cancels the seasonal pattern of sun angle and temperature). Needs two summers of data.
    /// </summary>
    public sealed record SolarHealth(List<SolarMonth> Months, int ComparedMonths, double? CurrentPercent,
                                     double? FadePercentPerYear, DateTime? EndOfLife)
    {
        /// <summary>Output at which panels count as worn out; the usual performance warranty after 25 years.</summary>
        public const double EndOfLifePercent = 80.0;

        /// <summary>Months with less theoretical yield per kWp than this are too dark to judge (winter, snow).</summary>
        public const double MinKWhPerKWp = 60.0;

        /// <summary>Year-on-year month pairs needed before the fade says anything.</summary>
        public const int MinComparedMonths = 6;

        /// <summary>Beyond this a forecast says nothing useful.</summary>
        private const double MaxForecastYears = 100.0;

        /// <summary>Monthly performance ratio, the year-on-year fade and when output reaches EndOfLifePercent.</summary>
        public static SolarHealth? Estimate(IReadOnlyDictionary<DateTime, double> solarKWhByQuarter,
                                            IReadOnlyDictionary<(DateTime Date, int Hour), double> radiationByHour,
                                            double kWp, DateTime now)
        {
            if (kWp <= 0.0 || solarKWhByQuarter.Count == 0 || radiationByHour.Count == 0)
                return null;

            var thisMonth = new DateTime(now.Year, now.Month, 1);

            var produced = solarKWhByQuarter
                .GroupBy(kv => new DateTime(kv.Key.Year, kv.Key.Month, 1))
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

            var theoretical = radiationByHour
                .GroupBy(kv => new DateTime(kv.Key.Date.Year, kv.Key.Date.Month, 1))
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value / 1000.0 * kWp));

            // Complete, bright enough months that have both production and radiation.
            var months = produced.Keys
                .Where(m => m < thisMonth && theoretical.TryGetValue(m, out var t) && t >= MinKWhPerKWp * kWp && produced[m] > 0.0)
                .OrderBy(m => m)
                .Select(m => new SolarMonth(m, produced[m] / theoretical[m]))
                .ToList();

            var byMonth = months.ToDictionary(m => m.Month, m => m.PerformanceRatio);
            var ratios = months
                .Where(m => byMonth.ContainsKey(m.Month.AddYears(-1)))
                .Select(m => m.PerformanceRatio / byMonth[m.Month.AddYears(-1)])
                .ToList();

            if (ratios.Count < MinComparedMonths)
                return new SolarHealth(months, ratios.Count, null, null, null);

            // Average year-on-year change, compounded over the years since the first month.
            var fade = (ratios.Average() - 1.0) * 100.0;
            var years = (months[^1].Month - months[0].Month).TotalDays / 365.25;
            var current = 100.0 * Math.Pow(1.0 + fade / 100.0, years);

            DateTime? endOfLife = null;
            if (fade < 0.0 && current > EndOfLifePercent)
            {
                var left = Math.Log(EndOfLifePercent / current) / Math.Log(1.0 + fade / 100.0);
                if (left <= MaxForecastYears)
                    endOfLife = now.Date.AddDays(left * 365.25);
            }
            else if (fade < 0.0)
                endOfLife = now.Date;

            return new SolarHealth(months, ratios.Count, current, fade, endOfLife);
        }
    }
}
