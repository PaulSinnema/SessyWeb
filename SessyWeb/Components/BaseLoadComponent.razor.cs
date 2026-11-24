using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Night-time base load per week: what the house draws when everything should be quiet.</summary>
    public partial class BaseLoadComponent : BaseComponent
    {
        [Inject]
        private ConsumptionPatternService? ConsumptionPatternService { get; set; }

        private string HowToRead => T("The base load is what the house draws when nobody is doing anything: fridge, freezer, router, standby " +
            "devices. Per night between 00:00 and 06:00 the quietest hour is taken; per week the middle (median) of " +
            "those nights is shown, so one restless night does not count. A line that creeps up means devices were " +
            "added that are always on. Every 10 W more is about 88 kWh a year.");

        private BaseLoadResult? _result;

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (ConsumptionPatternService == null) return;

            await WhileLoadingAsync(async () =>
            {
                _result = await Task.Run(() => ConsumptionPatternService.GetBaseLoadAsync()).ConfigureAwait(true);
            });
        }

        private string SummaryText()
        {
            var provider = GetFormatProvider();
            var now = _result!.RecentW ?? 0.0;
            var text = T("Last four weeks: {0:0} W.", now);

            var reference = _result.YearAgoW ?? _result.FirstW;
            var label = _result.YearAgoW.HasValue ? T("a year ago") : T("at the start of recording");

            if (reference.HasValue)
                text += T(" Four weeks {0}: {1:0} W ({2:+0;-0;0} W, about {3:+0;-0;0} kWh a year).",
                    label, reference.Value, now - reference.Value, (now - reference.Value) * 8.76);

            return text;
        }
    }
}
