using Microsoft.AspNetCore.Components;
using Radzen;
using SessyController.Services;

namespace SessyWeb.Pages
{
    public partial class PlannerAnalysisPage : PageBase
    {
        [Inject] private PlannerAnalysisService? _analysisService { get; set; }

        private PlannerAnalysis? _analysis;
        private bool _busy;

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (_analysisService == null) return;

            _busy = true;
            StateHasChanged();
            try
            {
                _analysis = await _analysisService.BuildAsync();
            }
            finally
            {
                _busy = false;
                StateHasChanged();
            }
        }

        private static BadgeStyle ModeBadge(string mode) => mode switch
        {
            "Charging" => BadgeStyle.Info,
            "Discharging" => BadgeStyle.Success,
            _ => BadgeStyle.Base
        };

        private static BadgeStyle PriceBadge(string position) => position switch
        {
            "Cheap" => BadgeStyle.Success,
            "Expensive" => BadgeStyle.Danger,
            _ => BadgeStyle.Base
        };

        // Light-ish badge fills (info/success/warning) get white text from the theme — force dark
        // text on those for contrast. Base (dark) and Danger (deep red) keep the light text.
        private static string? BadgeTextStyle(BadgeStyle style) => style switch
        {
            BadgeStyle.Info or BadgeStyle.Success or BadgeStyle.Warning => "color:#212529;",
            _ => null
        };

        // Per-quarter facts and calculations for the expanded row detail.
        private static List<PlannerParam> QuarterFacts(PlannerQuarterAnalysis q)
        {
            double chargeKWh = q.ChargePowerW * 0.25 / 1000.0;
            double dischargeKWh = q.DischargePowerW * 0.25 / 1000.0;

            var list = new List<PlannerParam>
            {
                new("", "Planned power", $"{q.PlannedPowerW:F0} W"),
                new("", "Unthrottled power", $"{q.UnthrottledPowerW:F0} W"),
                new("", "SOC", $"{q.SocWh:F0} Wh ({q.SocPct:F0}%)"),
                new("", "Reserve floor", $"{q.MinSocWh:F0} Wh"),
                new("", "Solar forecast", $"{q.SolarForecastWh:F0} Wh/quarter"),
                new("", "Consumption forecast", $"{q.ConsumptionForecastW:F0} W"),
                new("", "Net load", $"{q.NetLoadWh:F0} Wh"),
                new("", "Cost basis", $"€ {q.CostBasisEur:F4}/kWh"),
                new("", "Buy / sell", $"€ {q.BuyPrice:F3} / € {q.SellPrice:F3}"),
                new("", "Highest buy price ahead", $"€ {q.MaxBuyAheadEur:F3}"),
            };

            if (chargeKWh > 0.0001)
                list.Add(new("", "Charge this quarter", $"{chargeKWh:F3} kWh · cost € {chargeKWh * q.BuyPrice:F4}"));

            if (dischargeKWh > 0.0001)
            {
                if (q.Mode == "Discharging")
                    list.Add(new("", "Discharge→grid this quarter", $"{dischargeKWh:F3} kWh · revenue € {dischargeKWh * q.SellPrice:F4}"));
                else
                    list.Add(new("", "Self-consumption this quarter", $"{dischargeKWh:F3} kWh · avoided purchase € {dischargeKWh * q.BuyPrice:F4}"));
            }

            return list;
        }
    }
}
