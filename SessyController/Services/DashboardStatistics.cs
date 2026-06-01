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
        // ── ROI ────────────────────────────────────────────────────────────────

        /// <summary>Total net investment across all components (EUR).</summary>
        public double TotalNetInvestmentEur { get; set; }

        /// <summary>Total realized savings to date (EUR).</summary>
        public double TotalRealizedSavingsEur { get; set; }

        /// <summary>Projected total savings (realized + seasonal-average projection for missing periods) (EUR).</summary>
        public double ProjectedTotalSavingsEur { get; set; }

        /// <summary>Remaining investment to recover (EUR).</summary>
        public double RemainingInvestmentEur => Math.Max(0, TotalNetInvestmentEur - Math.Max(ProjectedTotalSavingsEur, TotalRealizedSavingsEur));

        /// <summary>Percentage of investment recovered.</summary>
        public double RecoveredPct => TotalNetInvestmentEur > 0
            ? Math.Min(100.0, Math.Max(ProjectedTotalSavingsEur, TotalRealizedSavingsEur) / TotalNetInvestmentEur * 100.0)
            : 0.0;

        /// <summary>
        /// Projected annual savings — sum of all component seasonal averages.
        /// This is the authoritative annual savings figure used everywhere.
        /// </summary>
        public double TotalAnnualSavingsEur { get; set; }

        /// <summary>Annual savings for the energy system (solar + battery combined), from seasonal averages.</summary>
        public double EnergySystemAnnualSavingsEur { get; set; }

        /// <summary>Annual savings for the heat pump, from HeatPumpStatistics.</summary>
        public double HeatPumpAnnualSavingsEur { get; set; }

        /// <summary>Projected payback period in years.</summary>
        public double ProjectedPaybackYears => TotalAnnualSavingsEur > 0
            ? TotalNetInvestmentEur / TotalAnnualSavingsEur
            : double.MaxValue;

        /// <summary>Projected break-even date.</summary>
        public DateTime? ProjectedBreakEvenDate { get; set; }

        /// <summary>True when seasonal projections fill gaps before available data.</summary>
        public bool UsesProjection { get; set; }

        /// <summary>Start date of available measurement data.</summary>
        public DateTime DataAvailableFrom { get; set; }

        /// <summary>Per-investment component breakdown (for the detail table).</summary>
        public List<InvestmentCategoryStats> ComponentBreakdown { get; set; } = new();

        // ── Period measurements (selected period only) ──────────────────────

        /// <summary>Start of the selected period.</summary>
        public DateTime PeriodStart { get; set; }

        /// <summary>End of the selected period.</summary>
        public DateTime PeriodEnd { get; set; }

        /// <summary>Number of days with data in the selected period.</summary>
        public double PeriodDays { get; set; }

        /// <summary>Total energy savings vs baseline in the selected period (EUR).</summary>
        public double PeriodSavingsEur { get; set; }

        /// <summary>Total solar production in the selected period (kWh).</summary>
        public double TotalSolarProductionKWh { get; set; }

        /// <summary>Self-sufficiency in the selected period (%).</summary>
        public double SelfSufficiencyPct { get; set; }
        public double SelfConsumptionPct { get; set; }

        /// <summary>Solar performance ratio in the selected period (ratio, multiply by 100 for %).</summary>
        public double SolarPerformanceRatio { get; set; }

        /// <summary>Average daily solar production in the selected period (kWh/day).</summary>
        public double AvgDailySolarProductionKWh { get; set; }

        /// <summary>Peak daily solar production in the selected period (kWh).</summary>
        public double PeakDailySolarProductionKWh { get; set; }

        /// <summary>Battery arbitrage profit realized in the selected period (EUR).</summary>
        public double ArbitrageProfitEur { get; set; }

        /// <summary>Battery arbitrage profit planned (MILP) in the selected period (EUR).</summary>
        public double PlannedArbitrageProfitEur { get; set; }

        /// <summary>
        /// Realized vs planned arbitrage ratio in the selected period (%).
        /// Greater than 100% when unforecast price spikes boosted realized profit.
        /// </summary>
        public double PlanExecutionPct => PlannedArbitrageProfitEur > 0
            ? ArbitrageProfitEur / PlannedArbitrageProfitEur * 100.0
            : 0.0;

        /// <summary>Revenue from grid export in the selected period (EUR).</summary>
        public double GridExportRevenueEur { get; set; }

        /// <summary>Total grid export in the selected period (kWh).</summary>
        public double TotalGridExportKWh { get; set; }

        /// <summary>Weighted average buy price in the selected period (EUR/kWh).</summary>
        public double WeightedAvgBuyPriceEurPerKWh { get; set; }

        /// <summary>Weighted average sell price in the selected period (EUR/kWh).</summary>
        public double WeightedAvgSellPriceEurPerKWh { get; set; }

        /// <summary>Total energy charged into batteries in the selected period (kWh).</summary>
        public double TotalBatteryChargedKWh { get; set; }

        /// <summary>Total energy discharged from batteries in the selected period (kWh).</summary>
        public double TotalBatteryDischargedKWh { get; set; }

        /// <summary>Battery round-trip efficiency (reliable periods only) (%).</summary>
        public double BatteryRoundTripEfficiencyPct { get; set; }
        public double PlannedRoundTripEfficiencyPct { get; set; }
        public double AverageRoundTripEfficiencyPct { get; set; }

        /// <summary>Total equivalent battery cycles since installation.</summary>
        public double BatteryCycles { get; set; }

        /// <summary>Average equivalent battery cycles per day.</summary>
        public double BatteryCyclesPerDay { get; set; }

        /// <summary>Average state of charge in the selected period (%).</summary>
        public double AverageSocPct { get; set; }

        /// <summary>Average per-battery cycle count (total cycles / number of batteries).</summary>
        public double AvgCyclesPerBattery { get; set; }

        /// <summary>Total household consumption in the selected period (kWh).</summary>
        public double TotalConsumptionKWh { get; set; }

        /// <summary>Total grid import in the selected period (kWh).</summary>
        public double TotalGridImportKWh { get; set; }

        /// <summary>Average daily consumption in the selected period (kWh/day).</summary>
        public double AvgDailyConsumptionKWh { get; set; }

        /// <summary>Peak daily consumption in the selected period (kWh).</summary>
        public double PeakDailyConsumptionKWh { get; set; }

        /// <summary>Total weekday consumption in the selected period (kWh).</summary>
        public double WeekdayConsumptionKWh { get; set; }

        /// <summary>Total weekend consumption in the selected period (kWh).</summary>
        public double WeekendConsumptionKWh { get; set; }

        // ── Heat pump ───────────────────────────────────────────────────────

        /// <summary>True when heat pump configuration is available.</summary>
        public bool HeatPumpIsConfigured { get; set; }

        /// <summary>Effective gas price used for heat pump savings (EUR/m³).</summary>
        public double GasPriceEurPerM3 { get; set; }

        /// <summary>True when gas price comes from live Enever feed.</summary>
        public bool IsLiveGasPrice { get; set; }

        /// <summary>Annual gas consumption before heat pump (m³/year).</summary>
        public double AnnualGasConsumptionM3 { get; set; }

        /// <summary>Annual gas cost saved by heat pump (EUR/year).</summary>
        public double AnnualGasCostSavedEur { get; set; }

        /// <summary>Annual gas standing charge saved (EUR/year).</summary>
        public double GasStandingChargeEurPerYear { get; set; }

        /// <summary>Annual heat pump electricity consumption (kWh/year).</summary>
        public double AnnualElectricityConsumptionKWh { get; set; }

        /// <summary>Effective electricity price for heat pump calculation (EUR/kWh).</summary>
        public double EffectiveElectricityPriceEurPerKWh { get; set; }

        /// <summary>Annual electricity cost of the heat pump (EUR/year).</summary>
        public double AnnualElectricityCostEur { get; set; }

        /// <summary>Gas price source description.</summary>
        public string GasPriceSource { get; set; } = string.Empty;

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