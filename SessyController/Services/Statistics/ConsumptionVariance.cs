namespace SessyController.Services.Statistics
{
    /// <summary>One category (day or month) of a comparison; From/To are the first and last compared current days.</summary>
    public sealed record VarianceItem(string Label, ConsumptionDifference Difference, int PreviousDays, int CurrentDays,
        DateTime? From = null, DateTime? To = null)
    {
        public bool HasData => PreviousDays > 0 && CurrentDays > 0;
    }

    /// <summary>
    /// All categories plus their total. NormalVariationKWh is twice the standard deviation of the
    /// model's day-to-day noise on the difference of the two periods: an "other" within ±that is noise.
    /// </summary>
    public sealed record VarianceResult(List<VarianceItem> Items, ConsumptionDifference Total, int PreviousDays, int CurrentDays, double NormalVariationKWh,
        DateTime? From = null, DateTime? To = null);

    /// <summary>One month of measured against expected consumption.</summary>
    public sealed record ModelFitItem(string Label, double MeasuredKWh, double ExpectedKWh, double CumulativeDeviationKWh, int Days);

    /// <summary>Builds the "vs. previous year" and "measured vs expected" views from the consumption model.</summary>
    public static class ConsumptionVariance
    {
        /// <summary>One day against one earlier day; skipped when either is not complete.</summary>
        public static VarianceItem PairedItem(ConsumptionModel model, IReadOnlyDictionary<DateTime, WeatherDay> days, string label,
            DateTime previousDate, DateTime currentDate) =>
            PairedGroupItem(model, days, label, new[] { (previousDate, currentDate) });

        /// <summary>
        /// Day pairs (e.g. a month against the same dates a year earlier). A pair counts only when both
        /// days are complete, so both sides always have the same number of days.
        /// </summary>
        public static VarianceItem PairedGroupItem(ConsumptionModel model, IReadOnlyDictionary<DateTime, WeatherDay> days, string label,
            IEnumerable<(DateTime Previous, DateTime Current)> pairs)
        {
            var used = pairs
                .Select(p => (Previous: p.Previous.Date, Current: p.Current.Date))
                .Distinct()
                .Where(p => days.ContainsKey(p.Previous) && days.ContainsKey(p.Current))
                .ToList();

            if (used.Count == 0)
                return new VarianceItem(label, new ConsumptionDifference(0, 0, 0, 0, 0), 0, 0);

            var previous = used.Select(p => days[p.Previous]).ToList();
            var current = used.Select(p => days[p.Current]).ToList();

            return new VarianceItem(label, model.Compare(previous, current), previous.Count, current.Count,
                used.Min(p => p.Current), used.Max(p => p.Current));
        }

        public static VarianceResult Combine(ConsumptionModel model, List<VarianceItem> items)
        {
            var used = items.Where(i => i.HasData).ToList();
            var total = new ConsumptionDifference(
                used.Sum(i => i.Difference.PreviousKWh),
                used.Sum(i => i.Difference.CurrentKWh),
                used.Sum(i => i.Difference.TemperatureKWh),
                used.Sum(i => i.Difference.CalendarKWh),
                used.Sum(i => i.Difference.OtherKWh));
            var previousDays = used.Sum(i => i.PreviousDays);
            var currentDays = used.Sum(i => i.CurrentDays);

            return new VarianceResult(items, total, previousDays, currentDays,
                2.0 * model.RmseKWh * Math.Sqrt(previousDays + currentDays),
                used.Count > 0 ? used.Min(i => i.From) : null,
                used.Count > 0 ? used.Max(i => i.To) : null);
        }

        /// <summary>Measured against expected per calendar month, with the running sum of the difference.</summary>
        public static List<ModelFitItem> ModelFitPerMonth(ConsumptionModel model, IEnumerable<WeatherDay> days)
        {
            var cumulative = 0.0;

            return days
                .GroupBy(d => new DateTime(d.Date.Year, d.Date.Month, 1))
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var measured = g.Sum(d => d.ConsumptionKWh);
                    var expected = g.Sum(d => model.Predict(d));

                    cumulative += measured - expected;

                    return new ModelFitItem($"{g.Key:yyyy-MM}", measured, expected, cumulative, g.Count());
                })
                .ToList();
        }

    }
}
