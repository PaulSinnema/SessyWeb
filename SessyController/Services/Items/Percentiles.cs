namespace SessyController.Services.Items
{
    /// <summary>Small statistical helpers shared by the services that fit on measured data.</summary>
    internal static class Percentiles
    {
        /// <summary>
        /// Linear-interpolated percentile of a list that is already sorted ascending.
        /// Returns 0 for an empty list, so a caller with no data gets a neutral answer.
        /// </summary>
        internal static double Percentile(IReadOnlyList<double> ascending, double percentile)
        {
            if (ascending.Count == 0) return 0.0;
            if (ascending.Count == 1) return ascending[0];

            double position = Math.Clamp(percentile, 0.0, 100.0) / 100.0 * (ascending.Count - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);

            if (lower == upper) return ascending[lower];

            return ascending[lower] + (position - lower) * (ascending[upper] - ascending[lower]);
        }
    }
}
