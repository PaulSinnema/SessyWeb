using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;
using SessyData.Model;

namespace SessyWeb.Components
{
    public partial class PlanVsActualComponent : BaseComponent
    {
        [Parameter, EditorRequired] public DashboardStatistics? Dashboard { get; set; }

        private int _rangeHours = 48;

        private List<PlanVsActualChartPoint> _filtered = new();
        private PlanVsActualStats _stats = new();

        // X-axis tick step — coarser for longer ranges to avoid label overlap.
        private object _axisStep => _rangeHours <= 48
            ? TimeSpan.FromHours(4)
            : TimeSpan.FromHours(24);

        protected override void OnParametersSet()
        {
            ApplyFilter();
        }

        private void OnRangeChanged(int hours)
        {
            _rangeHours = hours;
            ApplyFilter();
        }

        /// <summary>
        /// Filters the pre-loaded 7-day chart points from Dashboard in memory.
        /// Recalculates stats from the filtered subset.
        /// </summary>
        private void ApplyFilter()
        {
            if (Dashboard == null)
                return;

            var cutoff = _timeZoneService!.Now.AddHours(-_rangeHours);

            _filtered = Dashboard.PlanVsActualChartPoints
                .Where(p => p.Time >= cutoff)
                .OrderBy(p => p.Time)
                .ToList();

            // Derive stats from the filtered subset directly from Dashboard.PlanVsActualStats
            // when the range is 7d (full window), otherwise approximate from filtered points.
            if (_rangeHours >= 168)
            {
                _stats = Dashboard.PlanVsActualStats;
            }
            else
            {
                var withActual = _filtered.Where(p => p.ActualSocPct >= 0).ToList();
                var matched = withActual.Count(p => p.ModeMatch);
                var curtailed = withActual.Count(p =>
                    !string.IsNullOrEmpty(p.CurtailmentMode) && p.CurtailmentMode != "None");

                _stats = new PlanVsActualStats
                {
                    QuarterCount = withActual.Count,
                    AvgSocDeviationPct = withActual.Any()
                        ? withActual.Average(p => Math.Abs(p.ActualSocPct - p.PlannedSocPct))
                        : 0.0,
                    MaxSocDeviationPct = withActual.Any()
                        ? withActual.Max(p => Math.Abs(p.ActualSocPct - p.PlannedSocPct))
                        : 0.0,
                    ModeAccuracyPct = withActual.Any()
                        ? matched * 100.0 / withActual.Count
                        : 0.0,
                    CurtailmentQuarters = curtailed
                };
            }
        }
    }
}