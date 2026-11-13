namespace SessyController.Services.Statistics
{
    /// <summary>
    /// Expected daily house consumption from the weather and the calendar:
    /// kWh/day = Base + PerHeatingDegree·max(0, HeatingBaseC − T) + PerCoolingDegree·max(0, T − CoolingBaseC) + WeekendEffect·weekend.
    /// T is the day's average outdoor temperature. Radiation and humidity were tested and add nothing
    /// measurable, so they are left out. The base temperatures are chosen by the fit, not fixed.
    /// </summary>
    public sealed record ConsumptionModel(
        double Base,
        double PerHeatingDegree,
        double PerCoolingDegree,
        double WeekendEffect,
        double HeatingBaseC,
        double CoolingBaseC,
        DateTime ReferenceStart,
        DateTime ReferenceEnd,
        int Days,
        double RSquared,
        double RmseKWh,
        double WeeklyErrorPercent)
    {
        /// <summary>Default reference period: the first year of complete days.</summary>
        public const int DefaultReferenceDays = 365;

        /// <summary>Fewest complete days in the reference period to fit at all.</summary>
        public const int MinReferenceDays = 60;

        /// <summary>Fewest days with heating/cooling degrees for that term to be fitted.</summary>
        public const int MinDegreeDays = 5;

        public const int MinHeatingBaseC = 8;
        public const int MaxHeatingBaseC = 18;
        public const int MinCoolingBaseC = 18;
        public const int MaxCoolingBaseC = 26;

        public double HeatingDegrees(double temperatureC) => Math.Max(0.0, HeatingBaseC - temperatureC);

        public double CoolingDegrees(double temperatureC) => Math.Max(0.0, temperatureC - CoolingBaseC);

        /// <summary>The part of a day's consumption caused by heating and cooling.</summary>
        public double TemperatureKWh(double temperatureC) =>
            PerHeatingDegree * HeatingDegrees(temperatureC) + PerCoolingDegree * CoolingDegrees(temperatureC);

        /// <summary>The weekday/weekend part of a day's consumption.</summary>
        public double CalendarKWh(DateTime date) => Base + (IsWeekend(date) ? WeekendEffect : 0.0);

        public double Predict(DateTime date, double temperatureC) => CalendarKWh(date) + TemperatureKWh(temperatureC);

        public double Predict(WeatherDay day) => Predict(day.Date, day.TemperatureC);

        public static bool IsWeekend(DateTime date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        /// <summary>
        /// Splits the difference between two sets of complete days (e.g. a week and the same week a
        /// year earlier) into temperature, calendar (number of days, weekend days) and the rest.
        /// Previous + Temperature + Calendar + Other = Current.
        /// </summary>
        public ConsumptionDifference Compare(IReadOnlyCollection<WeatherDay> previous, IReadOnlyCollection<WeatherDay> current)
        {
            var previousKWh = previous.Sum(d => d.ConsumptionKWh);
            var currentKWh = current.Sum(d => d.ConsumptionKWh);
            var temperatureKWh = current.Sum(d => TemperatureKWh(d.TemperatureC)) - previous.Sum(d => TemperatureKWh(d.TemperatureC));
            var calendarKWh = current.Sum(d => CalendarKWh(d.Date)) - previous.Sum(d => CalendarKWh(d.Date));

            return new ConsumptionDifference(previousKWh, currentKWh, temperatureKWh, calendarKWh,
                currentKWh - previousKWh - temperatureKWh - calendarKWh);
        }

        /// <summary>
        /// Fits the model on the complete days in [referenceStart, referenceEnd). Defaults to the first
        /// year of data. Tries every whole-degree pair of base temperatures and keeps the best fit.
        /// Null when there are too few days.
        /// </summary>
        public static ConsumptionModel? Fit(IReadOnlyList<WeatherDay> days, DateTime? referenceStart = null, DateTime? referenceEnd = null)
        {
            if (days.Count == 0)
                return null;

            var start = (referenceStart ?? days.Min(d => d.Date)).Date;
            var end = (referenceEnd ?? start.AddDays(DefaultReferenceDays)).Date;
            var reference = days.Where(d => d.Date >= start && d.Date < end).ToList();

            if (reference.Count < MinReferenceDays)
                return null;

            Candidate? best = null;

            for (var heatingBase = MinHeatingBaseC; heatingBase <= MaxHeatingBaseC; heatingBase++)
            {
                // Cooling base null = no cooling term (e.g. a reference period without warm days).
                foreach (var coolingBase in CoolingBases(heatingBase))
                {
                    var candidate = FitCandidate(reference, heatingBase, coolingBase);

                    if (candidate != null && (best == null || candidate.Sse < best.Sse))
                        best = candidate;
                }
            }

            if (best == null)
                return null;

            var model = new ConsumptionModel(
                best.Coefficients[0],
                best.Coefficients[1],
                best.CoolingBase.HasValue ? best.Coefficients[2] : 0.0,
                best.Coefficients[^1],
                best.HeatingBase,
                best.CoolingBase ?? MaxCoolingBaseC,
                start,
                end,
                reference.Count,
                0.0,
                0.0,
                0.0);

            var mean = reference.Average(d => d.ConsumptionKWh);
            var sst = reference.Sum(d => (d.ConsumptionKWh - mean) * (d.ConsumptionKWh - mean));

            return model with
            {
                RSquared = sst > 0.0 ? 1.0 - best.Sse / sst : 0.0,
                RmseKWh = Math.Sqrt(best.Sse / reference.Count),
                WeeklyErrorPercent = WeeklyError(model, reference)
            };
        }

        private static IEnumerable<int?> CoolingBases(int heatingBase)
        {
            for (var coolingBase = Math.Max(heatingBase, MinCoolingBaseC); coolingBase <= MaxCoolingBaseC; coolingBase++)
                yield return coolingBase;

            yield return null;
        }

        private sealed record Candidate(int HeatingBase, int? CoolingBase, double[] Coefficients, double Sse);

        private static Candidate? FitCandidate(List<WeatherDay> days, int heatingBase, int? coolingBase)
        {
            if (days.Count(d => d.TemperatureC < heatingBase) < MinDegreeDays)
                return null;

            if (coolingBase.HasValue && days.Count(d => d.TemperatureC > coolingBase.Value) < MinDegreeDays)
                return null;

            // Columns: 1, heating degrees, [cooling degrees], weekend.
            double[] Row(WeatherDay d)
            {
                var row = new List<double> { 1.0, Math.Max(0.0, heatingBase - d.TemperatureC) };

                if (coolingBase.HasValue)
                    row.Add(Math.Max(0.0, d.TemperatureC - coolingBase.Value));

                row.Add(IsWeekend(d.Date) ? 1.0 : 0.0);

                return row.ToArray();
            }

            var rows = days.Select(Row).ToList();
            var coefficients = LeastSquares(rows, days.Select(d => d.ConsumptionKWh).ToList());

            if (coefficients == null)
                return null;

            var sse = 0.0;

            for (var i = 0; i < rows.Count; i++)
            {
                var residual = days[i].ConsumptionKWh - rows[i].Zip(coefficients, (x, b) => x * b).Sum();
                sse += residual * residual;
            }

            return new Candidate(heatingBase, coolingBase, coefficients, sse);
        }

        /// <summary>Ordinary least squares via the normal equations; null when they are singular.</summary>
        public static double[]? LeastSquares(IReadOnlyList<double[]> rows, IReadOnlyList<double> y)
        {
            var n = rows[0].Length;
            var a = new double[n, n + 1];

            for (var r = 0; r < rows.Count; r++)
            {
                for (var i = 0; i < n; i++)
                {
                    for (var j = 0; j < n; j++)
                        a[i, j] += rows[r][i] * rows[r][j];

                    a[i, n] += rows[r][i] * y[r];
                }
            }

            // Gaussian elimination with partial pivoting.
            for (var col = 0; col < n; col++)
            {
                var pivot = col;

                for (var r = col + 1; r < n; r++)
                {
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                        pivot = r;
                }

                if (Math.Abs(a[pivot, col]) < 1e-9)
                    return null;

                if (pivot != col)
                {
                    for (var j = 0; j <= n; j++)
                        (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
                }

                for (var r = 0; r < n; r++)
                {
                    if (r == col)
                        continue;

                    var factor = a[r, col] / a[col, col];

                    for (var j = col; j <= n; j++)
                        a[r, j] -= factor * a[col, j];
                }
            }

            return Enumerable.Range(0, n).Select(i => a[i, n] / a[i, i]).ToArray();
        }

        /// <summary>Mean absolute error in percent over full ISO weeks: the noise band for weekly comparisons.</summary>
        private static double WeeklyError(ConsumptionModel model, List<WeatherDay> days)
        {
            var errors = days
                .GroupBy(d => (System.Globalization.ISOWeek.GetYear(d.Date), System.Globalization.ISOWeek.GetWeekOfYear(d.Date)))
                .Where(g => g.Count() == 7)
                .Select(g =>
                {
                    var predicted = g.Sum(d => model.Predict(d));
                    return predicted > 0.0 ? Math.Abs(g.Sum(d => d.ConsumptionKWh) - predicted) / predicted * 100.0 : 0.0;
                })
                .ToList();

            return errors.Count > 0 ? errors.Average() : 0.0;
        }
    }

    /// <summary>Difference between two periods: Previous + Temperature + Calendar + Other = Current (kWh).</summary>
    public sealed record ConsumptionDifference(double PreviousKWh, double CurrentKWh, double TemperatureKWh, double CalendarKWh, double OtherKWh);

    /// <summary>Fits <see cref="ConsumptionModel"/> on the stored consumption.</summary>
    public class ConsumptionModelService
    {
        private readonly WeatherCorrelationService _weatherCorrelationService;

        public ConsumptionModelService(WeatherCorrelationService weatherCorrelationService)
        {
            _weatherCorrelationService = weatherCorrelationService;
        }

        /// <summary>The model plus the complete days it was fitted on and can be compared with.</summary>
        public async Task<(ConsumptionModel? Model, List<WeatherDay> Days)> GetModelAsync(DateTime? referenceStart = null, DateTime? referenceEnd = null)
        {
            var days = await _weatherCorrelationService.GetDaysAsync();

            return (ConsumptionModel.Fit(days, referenceStart, referenceEnd), days);
        }
    }
}
