using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Per month the price spread the battery captured against the best spread the prices offered.</summary>
    public partial class PriceCaptureComponent : BaseComponent
    {
        [Inject]
        private PriceCaptureService? PriceCaptureService { get; set; }

        private const string HowToRead =
            "Spread: the difference between the price at which the battery discharged and the price at which it " +
            "charged, per kWh (buying prices incl. taxes). Captured is what the battery achieved. Best possible is what " +
            "the same charged and discharged kWh would have given in the cheapest and most expensive quarters, each at most the day's largest quarter, " +
            "of that day. The line shows the captured share. 100 % is out of reach in practice: the battery " +
            "cannot do everything at once, the plan works with forecasts and solar charging follows the sun, not the price.";

        private List<PriceCaptureMonth> _months = new();

        private List<PriceCaptureMonth> CaptureMonths => _months.Where(m => m.CapturePercent.HasValue).ToList();

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (PriceCaptureService == null || _timeZoneService == null) return;

            await WhileLoadingAsync(async () =>
            {
                var to = _timeZoneService.Now.Date;
                _months = await Task.Run(() => PriceCaptureService.GetAsync(to.AddYears(-1), to)).ConfigureAwait(true);
            });
        }

        private string SummaryText()
        {
            var kWh = _months.Sum(m => m.DischargedKWh);
            var captured = _months.Sum(m => m.CapturedEurPerKWh * m.DischargedKWh) / kWh;
            var best = _months.Sum(m => m.BestEurPerKWh * m.DischargedKWh) / kWh;

            return string.Format(GetFormatProvider(),
                "Last 12 months: captured € {0:0.000} of a possible € {1:0.000} per kWh ({2:0} %), over {3} days and {4:0} kWh discharged.",
                captured, best, best > 0.0 ? captured / best * 100.0 : 0.0, _months.Sum(m => m.Days), kWh);
        }
    }
}
