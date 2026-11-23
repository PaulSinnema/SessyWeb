using Microsoft.AspNetCore.Components;
using SessyCommon.Extensions;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Planned against realized arbitrage, per day, week or month depending on the length of the period.</summary>
    public partial class DailyArbitrageChartComponent : BaseComponent
    {
        [Parameter, EditorRequired]
        public DashboardStatistics? Dashboard { get; set; }

        /// <summary>Up to this many days the chart shows days; up to MaxWeekDays weeks; beyond that months.</summary>
        private const int MaxDayDays = 31;
        private const int MaxWeekDays = 183;

        private const string HowToRead =
            "Planned: what the plan expected to earn by charging cheap and discharging dear. Realized: what the measured " +
            "battery flows earned at the actual prices. Short periods show days, longer ones weeks or months so the bars " +
            "stay readable; hover a bar for the amounts.";

        /// <summary>One bar pair of the chart.</summary>
        public sealed record ArbitrageBucket(DateTime Start, string Label, double PlannedEur, double RealizedEur);

        private List<ArbitrageBucket> Buckets { get; set; } = new();

        private string PeriodTitle { get; set; } = "Day";

        private string SummaryText { get; set; } = string.Empty;

        protected override void OnParametersSet()
        {
            var days = Dashboard?.DailyArbitrageTrends ?? new List<DailyArbitrageTrend>();

            if (days.Count == 0)
            {
                Buckets = new();
                return;
            }

            var span = (days.Max(d => d.Date) - days.Min(d => d.Date)).TotalDays + 1;
            var provider = GetFormatProvider();

            Func<DateTime, DateTime> key;
            string format;

            if (span <= MaxDayDays)
                (key, format, PeriodTitle) = (d => d.Date, "ddd dd-MM", "Day");
            else if (span <= MaxWeekDays)
                (key, format, PeriodTitle) = (d => d.StartOfIsoWeek(), "'wk' dd-MM", "Week (starting Monday)");
            else
                (key, format, PeriodTitle) = (d => d.MonthStart(), "MMM yyyy", "Month");

            Buckets = days
                .GroupBy(d => key(d.Date))
                .OrderBy(g => g.Key)
                .Select(g => new ArbitrageBucket(g.Key, g.Key.ToString(format, provider),
                                                 Math.Round(g.Sum(d => d.PlannedEur), 2),
                                                 Math.Round(g.Sum(d => d.RealizedEur), 2)))
                .ToList();

            var planned = days.Sum(d => d.PlannedEur);
            var realized = days.Sum(d => d.RealizedEur);

            SummaryText = planned > 0.0
                ? string.Format(provider, "Planned € {0:F2}, realized € {1:F2} ({2:0} % of plan).", planned, realized, realized / planned * 100.0)
                : string.Format(provider, "Planned € {0:F2}, realized € {1:F2}.", planned, realized);
        }
    }
}
