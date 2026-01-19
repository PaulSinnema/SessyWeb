using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Services;
using SessyWeb.Pages;

namespace SessyWeb.Components
{
    public partial class ChargingHoursChartComponent : BaseComponent
    {
        [Inject]
        private TaxesDataService? _taxesDataService { get; set; }

        [Parameter]
        public List<QuarterlyInfoView> QuarterlyInfos { get; set; } = new List<QuarterlyInfoView>();

        public string _graphStyle = "min-width: 250px; visibility: hidden;";

        [Parameter]
        public string? GraphStyle { get; set; }

        public double ChartMin => -ChartMinMax;
        public double ChartMax => ChartMinMax;

        public double ChartMinMax => Math.Round(Math.Max(QuarterlyInfos.Max(qi => qi.Price), Math.Abs(QuarterlyInfos.Min(qi => qi.Price))) + 0.10, 1);

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
