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

        /// <summary>One bar of the life chart: years used and years left, label with end date and cause.</summary>
        public sealed record ComponentLife(string Label, double UsedYears, double RemainingYears);

        private string LifeInfo => T(
            "Per component: the years since purchase and the years left until the expected end of life. Batteries: the " +
            "earliest of rated cycles at the current pace, rated age and measured capacity reaching 70 % (details on the " +
            "Battery tab). Solar panels: the rated age, or earlier when the measured performance ratio falls to 80 %. " +
            "Everything else: the purchase date plus the lifetime entered with the investment.");

        /// <summary>Start, expected end and what decides it, for any component; null when unknown.</summary>
        private (DateTime Start, DateTime End, string Limit)? LifeOf(InvestmentCategoryStats component)
        {
            if (TimeZoneService == null)
                return null;

            var now = TimeZoneService.Now;

            if (Cycles?.Find(component.Category) is CycleSeries series)
            {
                var life = series.Lifetime(withEstimate: true, now);
                return life.End.HasValue && life.Start.HasValue ? (life.Start.Value, life.End.Value, LimitText(life.LimitedBy)) : null;
            }

            DateTime? ageEnd = component.ExpectedLifetimeYears > 0 ? component.InstallationDate.AddYears(component.ExpectedLifetimeYears) : null;
            DateTime? fadeEnd = component.Kind == SessyData.Model.InvestmentCategory.Solar ? Dashboard?.SolarHealth?.EndOfLife : null;

            if (fadeEnd.HasValue && (!ageEnd.HasValue || fadeEnd < ageEnd))
                return (component.InstallationDate, fadeEnd.Value, LimitText(LifeLimit.Capacity));

            return ageEnd.HasValue ? (component.InstallationDate, ageEnd.Value, LimitText(LifeLimit.Age)) : null;
        }

        /// <summary>Every component with a known end of life, earliest end first.</summary>
        private List<ComponentLife> ComponentLives
        {
            get
            {
                if (Dashboard == null || TimeZoneService == null)
                    return new();

                var now = TimeZoneService.Now;

                return Dashboard.Investment.ComponentBreakdown
                    .Select(c => (c.Category, Life: LifeOf(c)))
                    .Where(x => x.Life.HasValue)
                    .OrderBy(x => x.Life!.Value.End)
                    .Select(x => new ComponentLife(
                        $"{x.Category}: {x.Life!.Value.End:MM-yyyy} ({x.Life.Value.Limit})",
                        Math.Round(Math.Max(0.0, (now - x.Life.Value.Start).TotalDays / 365.25), 1),
                        Math.Round(Math.Max(0.0, (x.Life.Value.End - now).TotalDays / 365.25), 1)))
                    .ToList();
            }
        }

        private string LifeChartStyle => $"width: 100%; height: {80 + 45 * ComponentLives.Count}px;";

        /// <summary>The solar panels' measured ageing, or how much data it still needs.</summary>
        private string SolarHealthText()
        {
            if (Dashboard?.SolarHealth is not SolarHealth health)
                return string.Empty;

            return health.FadePercentPerYear is double fade
                ? T("Solar panels: output {0:0.0} % of the first measured year ({1:+0.0;-0.0} %/year, from {2} months compared with the year before).",
                    health.CurrentPercent, fade, health.ComparedMonths)
                : T("Solar panels: the ageing needs {0} months compared with the same month a year earlier; {1} so far.",
                    SolarHealth.MinComparedMonths, health.ComparedMonths);
        }

        /// <summary>Expected end of life of a component; "—" when unknown.</summary>
        private string EndOfLifeText(InvestmentCategoryStats component) =>
            LifeOf(component) is { } life ? $"{life.End:MM-yyyy} ({life.Limit})" : "—";
    }
}
