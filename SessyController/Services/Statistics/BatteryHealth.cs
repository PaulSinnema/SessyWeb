using SessyCommon.Extensions;
using SessyData.Model;

namespace SessyController.Services.Statistics
{
    /// <summary>Median usable capacity of one month, from the discharge runs that month.</summary>
    public sealed record CapacityMonth(DateTime Month, double CapacityKWh, int Runs, double Percent)
    {
        public string Label => Month.ToString("MMM yyyy");
    }

    /// <summary>
    /// Measured capacity of one battery over time. Percentages are relative to the first measured months, not to the
    /// nominal capacity: the measured energy is AC (after the inverter), so it never reaches the nominal DC figure.
    /// </summary>
    public sealed record CapacityHealth(double NominalKWh, double BaselineKWh, List<CapacityMonth> Months,
                                       double? CurrentPercent, double? FadePercentPerYear, DateTime? EndOfLife)
    {
        /// <summary>Remaining capacity at which a battery counts as worn out; the usual warranty threshold.</summary>
        public const double EndOfLifePercent = 70.0;
    }

    /// <summary>
    /// Estimates usable capacity from long discharges: energy delivered ÷ fraction of state of charge it took.
    /// Rows are stored with the last reading of each quarter, so a single run is rough; the monthly median and
    /// the trend over months are what count.
    /// </summary>
    public static class BatteryHealth
    {
        /// <summary>Quarters below this discharge power end a run (idle or noise).</summary>
        public const double MinDischargeWatts = 200.0;

        /// <summary>A run must use at least this share of the state of charge to say something about capacity.</summary>
        public const double MinSocDrop = 0.4;

        /// <summary>Estimates outside this share of nominal are measurement errors.</summary>
        private const double MinShareOfNominal = 0.4, MaxShareOfNominal = 1.3;

        /// <summary>Months with fewer runs are left out of the trend.</summary>
        public const int MinRunsPerMonth = 3;

        /// <summary>The trend needs this many months and this span before it says anything.</summary>
        public const int MinTrendMonths = 4;
        public const double MinTrendDays = 180.0;

        /// <summary>Months averaged for the baseline and for the current value.</summary>
        private const int EdgeMonths = 3;

        /// <summary>Beyond this a forecast says nothing useful.</summary>
        private const double MaxForecastYears = 100.0;

        private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

        /// <summary>One capacity estimate per long discharge run: (end of the run, usable kWh).</summary>
        public static List<(DateTime Time, double CapacityKWh)> Runs(IEnumerable<BatteryQuarterMeasurement> rows)
        {
            var ordered = rows.GroupBy(r => r.Time).Select(g => g.Last()).OrderBy(r => r.Time).ToList();
            var result = new List<(DateTime, double)>();

            int i = 1;
            while (i < ordered.Count)
            {
                if (ordered[i].PowerWatts < MinDischargeWatts || ordered[i].Time - ordered[i - 1].Time != Quarter)
                {
                    i++;
                    continue;
                }

                // The reading before the run gives the state of charge at its start.
                var before = ordered[i - 1];
                var energy = 0.0;
                var last = i;

                while (last < ordered.Count && ordered[last].PowerWatts >= MinDischargeWatts
                       && ordered[last].Time - ordered[last - 1].Time == Quarter)
                {
                    energy += ordered[last].DischargedKWh;
                    last++;
                }

                var end = ordered[last - 1];
                var nominalWh = end.CapacityWh > 0.0 ? end.CapacityWh : before.CapacityWh;

                if (nominalWh > 0.0)
                {
                    var drop = (before.StateOfChargeWh - end.StateOfChargeWh) / nominalWh;

                    if (drop >= MinSocDrop)
                    {
                        var capacity = energy / drop;
                        var nominal = nominalWh / 1000.0;

                        if (capacity >= nominal * MinShareOfNominal && capacity <= nominal * MaxShareOfNominal)
                            result.Add((end.Time, capacity));
                    }
                }

                i = last;
            }

            return result;
        }

        /// <summary>Monthly capacity, the fade per year and when it reaches EndOfLifePercent; null without runs.</summary>
        public static CapacityHealth? Estimate(IReadOnlyCollection<BatteryQuarterMeasurement> rows, DateTime now)
        {
            var runs = Runs(rows);
            if (runs.Count == 0)
                return null;

            var nominal = rows.Where(r => r.CapacityWh > 0.0).Select(r => r.CapacityWh / 1000.0).DefaultIfEmpty(0.0).Max();

            var months = runs
                .GroupBy(r => r.Time.MonthStart())
                .OrderBy(g => g.Key)
                .Select(g => (Month: g.Key, Capacity: Median(g.Select(r => r.CapacityKWh)), Runs: g.Count()))
                .ToList();

            var usable = months.Where(m => m.Runs >= MinRunsPerMonth).ToList();
            if (usable.Count == 0)
                return new CapacityHealth(nominal, 0.0, months.Select(m => new CapacityMonth(m.Month, m.Capacity, m.Runs, 0.0)).ToList(),
                                          null, null, null);

            var baseline = usable.Take(EdgeMonths).Average(m => m.Capacity);
            double Percent(double kWh) => kWh / baseline * 100.0;

            var list = months.Select(m => new CapacityMonth(m.Month, m.Capacity, m.Runs, Percent(m.Capacity))).ToList();
            var current = Percent(usable.TakeLast(EdgeMonths).Average(m => m.Capacity));

            double? fade = null;
            DateTime? endOfLife = null;

            var span = (usable[^1].Month - usable[0].Month).TotalDays;
            if (usable.Count >= MinTrendMonths && span >= MinTrendDays)
            {
                // Least squares of percent against years, each month at its middle.
                var points = usable.Select(m => (X: (m.Month.AddDays(15) - usable[0].Month).TotalDays / 365.25, Y: Percent(m.Capacity))).ToList();
                var mx = points.Average(p => p.X);
                var my = points.Average(p => p.Y);
                var sxx = points.Sum(p => (p.X - mx) * (p.X - mx));
                var slope = sxx > 0.0 ? points.Sum(p => (p.X - mx) * (p.Y - my)) / sxx : 0.0;

                fade = slope;

                if (slope < 0.0)
                {
                    // Where the line crosses the threshold, counted from today's value on the line.
                    var nowX = (now - usable[0].Month).TotalDays / 365.25;
                    var onLineNow = my + slope * (nowX - mx);
                    var years = (onLineNow - CapacityHealth.EndOfLifePercent) / -slope;

                    if (years <= MaxForecastYears)
                        endOfLife = now.Date.AddDays(Math.Max(0.0, years) * 365.25);
                }
            }

            return new CapacityHealth(nominal, baseline, list, current, fade, endOfLife);
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }
}
