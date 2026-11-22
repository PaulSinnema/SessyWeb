using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    public partial class EnergySystemSavingsComponent
    {
        [Parameter, EditorRequired] public DashboardStatistics? Dashboard { get; set; }

        /// <summary>Cycle history per battery, for the lifetime average.</summary>
        [Parameter] public CycleHistoryResult? Cycles { get; set; }

        private string AvgCyclesPerBattery => Cycles is null ? "—" : $"{Cycles.AveragePerBattery(withEstimate: true):F0}";

        private string RoundTripEfficiencyValue =>
            $"{Dashboard?.Period.BatteryRoundTripEfficiencyPct:F1} %";

        private string RoundTripEfficiencyTooltip =>
            "Measured round-trip efficiency: discharged energy divided by charged energy, "
            + "SOC-corrected over the period. This is the real hardware efficiency including "
            + "all charge/discharge (Charging, Discharging and ZeroNetHome).";
    }
}