using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using SessyCommon.Extensions;
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
        public List<QuarterlyInfoView> QuarterlyInfos { get; set; } = new();

        [Parameter]
        public bool ShowAll { get; set; }

        // Annotation data point — only set when the exact current quarter exists in the series.
        // Must be a reference to an item from QuarterlyInfos so Radzen can position it correctly.
        public QuarterlyInfoView? NowQuarter { get; private set; }

        [Parameter]
        public string? GraphStyle { get; set; }

        public RadzenChart? QuarterlyHourChart { get; set; }

        public bool ShowSellingPriceLabels { get; set; }

        public double ChartMin => -ChartMinMax;
        public double ChartMax => ChartMinMax;

        private string GetBuyingPriceFill(object obj)
        {
            var qi = (QuarterlyInfo)obj;
            return qi.IsPriceExpected ? "#324ab255" : "#324ab2";
        }

        private string GetSellingPriceFill(object obj)
        {
            var qi = (QuarterlyInfo)obj;
            return qi.IsPriceExpected ? "#00aae455" : "#00aae4";
        }

        private bool IsExpected(List<QuarterlyInfoView> quarterlyInfos)
        {
            return quarterlyInfos != null && quarterlyInfos.Any(q => q.IsPriceExpected);
        }

        public double ChartMinMax
        {
            get
            {
                if (QuarterlyInfos == null || QuarterlyInfos.Count == 0)
                    return 1.0;

                var max = QuarterlyInfos.Max(qi => qi.Price);
                var min = QuarterlyInfos.Min(qi => qi.Price);

                return Math.Round(Math.Max(max, Math.Abs(min)) + 0.20, 1);
            }
        }

        protected override async Task OnParametersSetAsync()
        {
            var now = _timeZoneService!.Now;

            var taxes = await _taxesDataService!.GetTaxesForDate(now).ConfigureAwait(false);
            if (taxes != null)
                ShowSellingPriceLabels = !taxes.Netting;

            // Find the exact current quarter in the series and set its NowLineHeight to ChartMax
            // so the vertical bar spans the full chart height.
            NowQuarter = QuarterlyInfos.FirstOrDefault(q => q.Time == now.DateFloorQuarter());
            if (NowQuarter != null)
                NowQuarter.NowLineHeight = ChartMax;
        }
    }
}