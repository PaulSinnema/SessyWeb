using SessyCommon.Extensions;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Helpers
{
    /// <summary>The time span a chosen period covers: Start inclusive, End exclusive.</summary>
    public readonly record struct PeriodRange(DateTime Start, DateTime End)
    {
        /// <summary>The day, ISO week, month or year that holds the date; All covers everything.</summary>
        public static PeriodRange For(PeriodsEnums period, DateTime date) => period switch
        {
            PeriodsEnums.Day => new(date.Date, date.Date.AddDays(1)),
            PeriodsEnums.Week => new(date.StartOfIsoWeek(), date.StartOfIsoWeek().AddDays(7)),
            PeriodsEnums.Month => new(date.MonthStart(), date.MonthStart().AddMonths(1)),
            PeriodsEnums.Year => new(new DateTime(date.Year, 1, 1), new DateTime(date.Year + 1, 1, 1)),
            PeriodsEnums.All => new(DateTime.MinValue, DateTime.MaxValue),
            _ => throw new InvalidOperationException($"Period {period} has no fixed range")
        };

        /// <summary>The last second inside the range, for filters that compare with &lt;=.</summary>
        public DateTime LastSecond => End == DateTime.MaxValue ? End : End.AddSeconds(-1);
    }
}
