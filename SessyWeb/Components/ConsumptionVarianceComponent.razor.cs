using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;
using System.Globalization;

namespace SessyWeb.Components
{
    /// <summary>
    /// "vs. previous year" tab of the Consumption page: a waterfall from last year's to this year's
    /// consumption (temperature and other) and the difference per day or month as diverging stacked bars.
    /// For All it shows measured against expected consumption per month instead.
    /// </summary>
    public partial class ConsumptionVarianceComponent : ComponentBase
    {
        [Parameter] public ConsumptionModel? Model { get; set; }

        [Parameter] public VarianceResult? Result { get; set; }

        /// <summary>Set for All: measured against expected per month.</summary>
        [Parameter] public List<ModelFitItem>? Fit { get; set; }

        [Parameter] public string PeriodText { get; set; } = string.Empty;

        /// <summary>Axis title of the difference chart: "Day" or "Month".</summary>
        [Parameter] public string CategoryTitle { get; set; } = string.Empty;

        /// <summary>Shown instead of the charts when nothing can be compared.</summary>
        [Parameter] public string? Message { get; set; }

        /// <summary>One column of the waterfall; IsTotal columns start at zero.</summary>
        public sealed record WaterfallStep(string Label, double KWh, bool IsTotal);

        /// <summary>Flat row for Radzen: it cannot bind to nested properties. Calendar is counted as other.</summary>
        public sealed record DifferenceRow(string Label, double TemperatureKWh, double OtherKWh);

        private const string HowToRead =
            "Left: last year's consumption over the compared days. Right: this year's. In between, the difference is " +
            "split up. Temperature: what the model expects the colder or warmer weather to cost or save. Other: what is " +
            "left, the part you influence yourself (appliances, habits, who is home). Red adds, green saves. Small " +
            "amounts of 'other' are normal day-to-day variation.";

        private const string DifferenceInfo =
            "Per day or month, how much more (right) or less (left) was used than last year, built from the temperature " +
            "part and the other part. Hover a bar for the numbers. Days that are not complete in both years are left out.";

        private const string FitInfo =
            "Bars: what the house used per month. Line: what the model expects for that month's weather and calendar. " +
            "The cumulative difference adds up measured minus expected; a line that keeps rising or falling means a " +
            "lasting change, for example a new appliance or a different way of living.";

        private List<WaterfallStep> Steps { get; set; } = new();
        private List<DifferenceRow> Rows { get; set; } = new();

        protected override void OnParametersSet()
        {
            Steps = new();
            Rows = new();

            if (Result == null)
                return;

            var total = Result.Total;

            Steps =
            [
                new("Last year", total.PreviousKWh, false),
                new("Temperature", total.TemperatureKWh, false),
                new("Other", OtherKWh(total), false),
                new("This year", total.CurrentKWh, true)
            ];

            // Days or months without data in both years are left out; first category on top.
            Rows = Result.Items
                .Where(i => i.HasData)
                .Select(i => new DifferenceRow(i.Label, i.Difference.TemperatureKWh, OtherKWh(i.Difference)))
                .Reverse()
                .ToList();
        }

        private bool ShowDifferenceChart => Rows.Count > 1;

        // Rows of 26 px so a month of days stays readable.
        private string DifferenceChartStyle => $"width: 100%; height: {Math.Max(260, Rows.Count * 26 + 90)}px;";

        // Calendar (weekend days) is a few kWh at most: shown as part of other.
        private static double OtherKWh(ConsumptionDifference difference) => difference.OtherKWh + difference.CalendarKWh;

        private static string Kwh(double value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " kWh";

        private string ComparedText() => Result?.From == null ? string.Empty : string.Format(CultureInfo.CurrentCulture,
            "Compared: {0:dd-MM-yyyy} – {1:dd-MM-yyyy} against the same days a year earlier ({2} days). " +
            "Only days that are complete in both years count; today is left out until it is complete.",
            Result.From, Result.To, Result.CurrentDays);

        private string OtherText()
        {
            var total = Result!.Total;
            var other = OtherKWh(total);
            var expected = total.CurrentKWh - other;
            var percent = expected > 0 ? other / expected * 100.0 : 0.0;
            var verdict = Math.Abs(other) <= Result.NormalVariationKWh
                ? "within the normal day-to-day variation"
                : other > 0 ? "more than normal variation explains" : "less than normal variation explains";

            return string.Format(CultureInfo.CurrentCulture,
                "Other: {0} ({1:+0.0;-0.0;0.0} % of what the weather leads to expect), {2} (±{3:0} kWh).",
                Kwh(other), percent, verdict, Result.NormalVariationKWh);
        }

        private string ModelText() => Model == null ? string.Empty : string.Format(CultureInfo.CurrentCulture,
            "Model: {0:0.0} kWh/day base, +{1:0.00} kWh per °C below {2} °C, +{3:0.00} kWh per °C above {4} °C, {5:+0.0;-0.0} kWh on weekend days; " +
            "fitted on {6:dd-MM-yyyy} – {7:dd-MM-yyyy} ({8} days, R² {9:0.00}).",
            Model.Base, Model.PerHeatingDegree, Model.HeatingBaseC, Model.PerCoolingDegree, Model.CoolingBaseC, Model.WeekendEffect,
            Model.ReferenceStart, Model.ReferenceEnd.AddDays(-1), Model.Days, Model.RSquared);
    }
}
