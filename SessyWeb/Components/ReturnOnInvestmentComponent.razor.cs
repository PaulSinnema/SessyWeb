using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    public partial class ReturnOnInvestmentComponent
    {
        [Parameter, EditorRequired] public DashboardStatistics? Dashboard { get; set; }

        /// <summary>Cycle history per battery; the cycles column reads from it.</summary>
        [Parameter] public CycleHistoryResult? Cycles { get; set; }

        /// <summary>Lifetime cycles of the battery this investment is, estimate included; null for anything else.</summary>
        private double? CyclesOf(InvestmentCategoryStats component) =>
            Cycles?.Find(component.Category)?.TotalCycles(withEstimate: true);
    }
}
