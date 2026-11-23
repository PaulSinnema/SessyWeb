using Microsoft.AspNetCore.Components;
using SessyCommon.Services;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    public partial class ReturnOnInvestmentComponent
    {
        [Parameter, EditorRequired] public DashboardStatistics? Dashboard { get; set; }

        /// <summary>Cycle history per battery; the cycles column reads from it.</summary>
        [Parameter] public CycleHistoryResult? Cycles { get; set; }

        /// <summary>Lifetime cycles of the battery this investment is, estimate included; null for anything else.</summary>
        private double? CyclesOf(InvestmentCategoryStats component) =>
            Cycles?.Find(component.Category)?.TotalCycles(withEstimate: true);

        [Inject] private TimeZoneService? TimeZoneService { get; set; }

        /// <summary>One bar of the battery life chart: years used and years left, label with end date and cause.</summary>
        public sealed record BatteryLife(string Label, double UsedYears, double RemainingYears);

        private const string BatteryLifeInfo =
            "Per battery: the years since purchase and the years left until the expected end of life, the earliest of " +
            "rated cycles at the current pace, rated age and measured capacity reaching 70 %. Details on the Battery tab.";

        /// <summary>Every battery with a known end of life, earliest end first.</summary>
        private List<BatteryLife> BatteryLives
        {
            get
            {
                if (Cycles == null || TimeZoneService == null)
                    return new();

                var now = TimeZoneService.Now;

                return Cycles.Lifetimes(withEstimate: true, now)
                    .Where(l => l.End.HasValue && l.Start.HasValue)
                    .OrderBy(l => l.End)
                    .Select(l => new BatteryLife(
                        $"{l.Name}: {l.End:MM-yyyy} ({l.LimitedBy.ToString().ToLowerInvariant()})",
                        Math.Round(Math.Max(0.0, (now - l.Start!.Value).TotalDays / 365.25), 1),
                        Math.Round(Math.Max(0.0, (l.End!.Value - now).TotalDays / 365.25), 1)))
                    .ToList();
            }
        }

        private string BatteryLifeChartStyle => $"width: 100%; height: {80 + 45 * BatteryLives.Count}px;";

        /// <summary>Expected end of life of the battery this investment is (see the Battery tab); "—" for anything else.</summary>
        private string EndOfLifeText(InvestmentCategoryStats component)
        {
            if (Cycles?.Find(component.Category) is not CycleSeries series || TimeZoneService == null)
                return "—";

            var life = series.Lifetime(withEstimate: true, TimeZoneService.Now);

            return life.End.HasValue
                ? $"{life.End.Value:MM-yyyy} ({life.LimitedBy.ToString().ToLowerInvariant()})"
                : "—";
        }
    }
}
