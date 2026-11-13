using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Per day: measured consumption against the planner's day-ahead forecast and the consumption model.</summary>
    public partial class ForecastAccuracyComponent : BaseComponent
    {
        [Inject]
        private ForecastAccuracyService? ForecastAccuracyService { get; set; }

        private const string HowToRead =
            "Bars: what the house used each day. Pink line: what the planner expected for that day, a day in advance; " +
            "this is the estimate the battery plan is built on. Orange line: what the consumption model predicts from " +
            "that day's measured temperature and the day of the week. The model gets the real temperature, as if the " +
            "weather forecast were perfect, so it has a head start. Mean error: how far off a forecast is on average. " +
            "Bias: whether it is structurally too high (+) or too low (−). The planner's forecasts are kept 40 days.";

        private bool _loading = true;
        private ForecastAccuracyResult? _result;

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (ForecastAccuracyService == null) return;

            _loading = true;

            try
            {
                // Off the circuit thread: the model is fitted on all stored consumption.
                _result = await Task.Run(() => ForecastAccuracyService.GetAsync()).ConfigureAwait(true);
            }
            finally
            {
                _loading = false;
            }
        }

        private string SummaryText() => string.Format(GetFormatProvider(),
            "{0} complete days, {1:dd-MM-yyyy} to {2:dd-MM-yyyy}.",
            _result!.Days.Count, _result.Days[0].Date, _result.Days[^1].Date);

        private string ErrorText(string name, ForecastError? error) => error == null
            ? $"{name}: not available."
            : string.Format(GetFormatProvider(), "{0}: mean error {1:0.0} %, bias {2:+0.0;-0.0;0.0} %.",
                name, error.MeanAbsolutePercent, error.BiasPercent);
    }
}
