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
        private TimeZoneService? _timeZoneService {  get; set; }

        [Inject]
        private TaxesDataService? _taxesDataService { get; set; }

        [Parameter]
        public List<QuarterlyInfoView> QuarterlyInfos { get; set; } = new List<QuarterlyInfoView>();

        public string _graphStyle = "min-width: 250px; visibility: hidden;";

        [Parameter]
        public string? GraphStyle { get; set; }

        public RadzenChart? QuarterlyHourChart { get; set; }

        protected override async Task OnParametersSetAsync()
        {
            var now = _timeZoneService!.Now;

            var taxes = await _taxesDataService!.GetTaxesForDate(now);

            if (taxes != null)
            {
                ShowSellingPriceLabels = !taxes.Netting;
            }
        }

        public bool ShowSellingPriceLabels {  get; set; }
    }
}
