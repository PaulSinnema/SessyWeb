namespace SessyController.Services.Items
{
    /// <summary>Statistical helpers shared by the services that fit on measured data.</summary>
    public static class Stats
    {
        /// <summary>Median; 0 for an empty set, so a caller with no data gets a neutral answer.</summary>
        public static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();

            if (sorted.Count == 0)
                return 0.0;

            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }

        /// <summary>
        /// Linear-interpolated percentile of a list that is already sorted ascending.
        /// Returns 0 for an empty list, so a caller with no data gets a neutral answer.
        /// </summary>
        public static double Percentile(IReadOnlyList<double> ascending, double percentile)
        {
            if (ascending.Count == 0) return 0.0;
            if (ascending.Count == 1) return ascending[0];

            double position = Math.Clamp(percentile, 0.0, 100.0) / 100.0 * (ascending.Count - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);

            if (lower == upper) return ascending[lower];

            return ascending[lower] + (position - lower) * (ascending[upper] - ascending[lower]);
        }

        /// <summary>
        /// Ordinary least squares via the normal equations, solved with Gaussian elimination and
        /// partial pivoting. Optional per-row weights. Returns null when the system is singular or near-singular.
        /// </summary>
        public static double[]? LeastSquares(
            IReadOnlyList<double[]> design, IReadOnlyList<double> y, IReadOnlyList<double>? weights = null)
        {
            int k = design[0].Length;
            var matrix = new double[k, k + 1];

            for (int row = 0; row < k; row++)
            {
                for (int col = 0; col < k; col++)
                {
                    double sum = 0.0;
                    for (int i = 0; i < design.Count; i++)
                        sum += (weights?[i] ?? 1.0) * design[i][row] * design[i][col];
                    matrix[row, col] = sum;
                }

                double rhs = 0.0;
                for (int i = 0; i < design.Count; i++)
                    rhs += (weights?[i] ?? 1.0) * design[i][row] * y[i];
                matrix[row, k] = rhs;
            }

            for (int pivot = 0; pivot < k; pivot++)
            {
                int best = pivot;
                for (int row = pivot + 1; row < k; row++)
                    if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot]))
                        best = row;

                if (Math.Abs(matrix[best, pivot]) < 1e-9) return null;

                if (best != pivot)
                    for (int col = 0; col <= k; col++)
                        (matrix[pivot, col], matrix[best, col]) = (matrix[best, col], matrix[pivot, col]);

                for (int row = 0; row < k; row++)
                {
                    if (row == pivot) continue;
                    double factor = matrix[row, pivot] / matrix[pivot, pivot];
                    for (int col = pivot; col <= k; col++)
                        matrix[row, col] -= factor * matrix[pivot, col];
                }
            }

            var result = new double[k];
            for (int i = 0; i < k; i++)
                result[i] = matrix[i, k] / matrix[i, i];

            return result;
        }
    }
}
