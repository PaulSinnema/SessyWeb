using SessyData.Model;

namespace SessyController.Services.Statistics
{
    /// <summary>
    /// Single source of truth for the Energy Statistics dashboard.
    ///
    /// Design rules (enforced by EnergyStatisticsService.GetDashboardStatisticsAsync):
    ///
    ///   1. ANNUAL SAVINGS — always from seasonal monthly averages (all historical data).
    ///      Never extrapolated from the selected period alone.
    ///
    ///   2. PERIOD METRICS — always from actual measurements in the selected period.
    ///      Only used to show "what happened", never for projections.
    ///
    ///   3. ONE VALUE per concept — no duplicate properties with different calculation methods.
    ///      The view reads this object and displays values as-is, no interpretation.
    ///
    /// The view (EnergyStatisticsPage.razor) binds exclusively to this class.
    /// </summary>
    public class DashboardStatistics
    {
        /// <summary>Lifetime return on investment, from seasonal averages over all data.</summary>
        public InvestmentStatistics Investment { get; init; } = new();

        /// <summary>What was measured in the selected period.</summary>
        public EnergyStatistics Period { get; init; } = new();

        /// <summary>Heat pump savings with the resolved gas and electricity prices.</summary>
        public HeatPumpStatistics HeatPump { get; init; } = new();

        /// <summary>Annual savings for the energy system (solar + battery combined), from seasonal averages.</summary>
        public double EnergySystemAnnualSavingsEur { get; set; }

        /// <summary>
        /// True when an inverter is configured. Everything solar is hidden without one:
        /// production, self-consumption and the performance ratio can then only read zero, and
        /// self-sufficiency measures what solar covers, which is nothing.
        /// </summary>
        public bool SolarIsConfigured { get; set; }

        /// <summary>Ageing of the solar panels from the performance ratio; null without solar data.</summary>
        public SessyController.Services.Statistics.SolarHealth? SolarHealth { get; set; }

        /// <summary>True when heat pump configuration is available.</summary>
        public bool HeatPumpIsConfigured { get; set; }

        /// <summary>
        /// Cycle cost in EUR/kWh, derived from battery investments
        /// (net purchase price / expected energy throughput).
        /// </summary>
        public double CycleCostEurPerKWh { get; set; }

        // ── Charts ──────────────────────────────────────────────────────────

        /// <summary>Daily arbitrage: planned vs realized (for chart).</summary>
        public List<DailyArbitrageTrend> DailyArbitrageTrends { get; set; } = new();

        // ── Plan vs Actual ──────────────────────────────────────────────────

        /// <summary>
        /// Pre-converted SOC chart points for the last 7 days.
        /// Planned and actual SOC are expressed as percentages (0–100).
        /// Populated by EnergyStatisticsService — independent of the date chooser period.
        /// </summary>
        public List<PlanVsActualChartPoint> PlanVsActualChartPoints { get; set; } = new();

        /// <summary>Aggregate plan vs actual statistics for the last 7 days.</summary>
        public PlanVsActualStats PlanVsActualStats { get; set; } = new();

        // ── Plan ────────────────────────────────────────────────────────────

        /// <summary>Statistics about the current MILP plan.</summary>
        public PlanStatistics? Plan { get; set; }
    }

    /// <summary>
    /// A single quarter's planned vs actual SOC, pre-converted to percentages.
    /// Used by PlanVsActualComponent for chart rendering without further calculation.
    /// </summary>
    public class PlanVsActualChartPoint
    {
        public DateTime Time { get; set; }
        public double PlannedSocPct { get; set; }
        public double ActualSocPct { get; set; }
        public string CurtailmentMode { get; set; } = string.Empty;
        public bool ModeMatch { get; set; }
    }
}