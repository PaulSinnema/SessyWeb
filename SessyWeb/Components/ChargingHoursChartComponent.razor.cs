using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyData.Services;
using SessyWeb.Pages;

namespace SessyWeb.Components
{
    public partial class ChargingHoursChartComponent : BaseComponent
    {
        [Inject]
        private TaxesDataService? _taxesDataService { get; set; }

        [Parameter]
        public List<QuarterlyInfoView> QuarterlyInfos { get; set; } = new();

        [Parameter]
        public string? GraphStyle { get; set; }

        public RadzenChart? QuarterlyHourChart { get; set; }

        public bool ShowSellingPriceLabels { get; set; }

        public double ChartMin => -ChartMinMax;
        public double ChartMax => ChartMinMax;

        public double ChartMinMax
        {
            get
            {
                if (QuarterlyInfos == null || QuarterlyInfos.Count == 0)
                    return 1.0;

                var max = QuarterlyInfos.Max(qi => qi.Price);
                var min = QuarterlyInfos.Min(qi => qi.Price);

                return Math.Round(Math.Max(max, Math.Abs(min)) + 0.10, 1);
            }
        }

        protected override async Task OnParametersSetAsync()
        {
            var now = _timeZoneService!.Now;

            var taxes = await _taxesDataService!.GetTaxesForDate(now).ConfigureAwait(false);
            if (taxes != null)
                ShowSellingPriceLabels = !taxes.Netting;
        }
    }
}
