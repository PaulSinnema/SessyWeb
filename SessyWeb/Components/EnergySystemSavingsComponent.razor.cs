using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    public partial class EnergySystemSavingsComponent
    {
        [Parameter, EditorRequired] public DashboardStatistics? Dashboard { get; set; }

        private string RoundTripEfficiencyValue =>
            $"{(Dashboard?.PlannedRoundTripEfficiencyPct > 0 ? $"{Dashboard.PlannedRoundTripEfficiencyPct:F1}" : "n/a")} / {Dashboard?.BatteryRoundTripEfficiencyPct:F1} / {Dashboard?.AverageRoundTripEfficiencyPct:F1} %";

        private string RoundTripEfficiencyTooltip =>
            Dashboard?.PlannedRoundTripEfficiencyPct <= 0
                ? "Planned efficiency requires at least 30 days of data — SOC imbalance over shorter periods makes the calculation unreliable."
                : string.Empty;
    }
}