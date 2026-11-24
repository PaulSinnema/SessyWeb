using Microsoft.AspNetCore.Components;
using Radzen;
using SessyController.Services;
using SessyCommon;

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
            try
            {
                // Texts the service builds come out in the page's language.
                using (SessyCommon.ServiceText.Use(T))
                    _analysis = await _analysisService.BuildAsync();
            }
            finally
            {
                _busy = false;
            }
        }

        private static BadgeStyle PriceBadge(string position) => position switch
        {
            "Cheap" => BadgeStyle.Success,
            "Expensive" => BadgeStyle.Danger,
            _ => BadgeStyle.Base
        };

        // Mode badges use SessyColors (chart colours); dark text on bright badges comes from site.css.

        // Per-quarter facts and calculations for the expanded row detail.
        private List<PlannerParam> QuarterFacts(PlannerQuarterAnalysis q)
        {
            double chargeKWh = Units.QuarterKWh(q.ChargePowerW);
            double dischargeKWh = Units.QuarterKWh(q.DischargePowerW);

            var list = new List<PlannerParam>
            {
                new("", T("Planned power"), $"{q.PlannedPowerW:F0} W"),
                new("", T("Unthrottled power"), $"{q.UnthrottledPowerW:F0} W"),
                new("", T("SOC"), $"{q.SocWh:F0} Wh ({q.SocPct:F0}%)"),
                new("", T("Reserve floor"), $"{q.MinSocWh:F0} Wh"),
                new("", T("Solar forecast"), $"{q.SolarForecastWh:F0} Wh/quarter"),
                new("", T("Consumption forecast"), $"{q.ConsumptionForecastW:F0} W"),
                new("", T("Net load"), $"{q.NetLoadWh:F0} Wh"),
                new("", T("Cost basis"), $"€ {q.CostBasisEur:F4}/kWh"),
                new("", T("Buy / Sell"), $"€ {q.BuyPrice:F3} / € {q.SellPrice:F3}"),
                new("", T("Highest buy price ahead"), $"€ {q.MaxBuyAheadEur:F3}"),
            };

            if (chargeKWh > 0.0001)
                list.Add(new("", T("Charge this quarter"), $"{chargeKWh:F3} kWh · {T("Cost")} € {chargeKWh * q.BuyPrice:F4}"));

            if (dischargeKWh > 0.0001)
            {
                if (q.Mode == "Discharging")
                    list.Add(new("", T("Discharge→grid this quarter"), $"{dischargeKWh:F3} kWh · {T("Revenue")} € {dischargeKWh * q.SellPrice:F4}"));
                else
                    list.Add(new("", T("Self-consumption this quarter"), $"{dischargeKWh:F3} kWh · {T("avoided purchase")} € {dischargeKWh * q.BuyPrice:F4}"));
            }

            return list;
        }
    }
}
