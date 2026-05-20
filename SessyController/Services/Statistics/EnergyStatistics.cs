namespace SessyController.Services.Statistics
{
    /// <summary>
    /// Energy flow statistics for a given period.
    /// All energy values are in kWh, all financial values in EUR.
    /// </summary>
    public class EnergyStatistics
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }

        // ── Energy flows ─────────────────────────────────────────────────────

        /// <summary>Total energy consumed from the grid (kWh).</summary>
        public double TotalGridImportKWh { get; set; }

        /// <summary>Total energy exported to the grid (kWh).</summary>
        public double TotalGridExportKWh { get; set; }

        /// <summary>Total solar energy produced (kWh).</summary>
        public double TotalSolarProductionKWh { get; set; }

        /// <summary>Total household consumption (kWh).</summary>
        public double TotalConsumptionKWh { get; set; }

        /// <summary>Energy consumed directly from solar without going through grid or battery (kWh).</summary>
        public double SelfConsumedSolarKWh { get; set; }

        /// <summary>
        /// Percentage of total consumption covered by own solar + battery.
        /// 100% = fully self-sufficient.
        /// </summary>
        public double SelfSufficiencyPct => TotalConsumptionKWh > 0
            // Self-sufficiency = share of consumption covered by own sources (solar + battery).
            // Clamp to [0, 100]: grid import can exceed consumption when battery charges from grid,
            // which would otherwise produce a negative percentage.
            ? Math.Clamp((TotalConsumptionKWh - TotalGridImportKWh) / TotalConsumptionKWh * 100.0, 0.0, 100.0)
            : 0.0;

        /// <summary>
        /// Percentage of solar production consumed on-site (not exported).
        /// 100% = all solar used locally.
        /// </summary>
        public double SelfConsumptionPct => TotalSolarProductionKWh > 0
            ? Math.Min(100.0, SelfConsumedSolarKWh / TotalSolarProductionKWh * 100.0)
            : 0.0;

        /// <summary>Percentage of total energy that came from the grid.</summary>
        public double GridDependencyPct => TotalConsumptionKWh > 0
            ? TotalGridImportKWh / TotalConsumptionKWh * 100.0
            : 0.0;

        // ── Battery statistics ───────────────────────────────────────────────

        /// <summary>Total energy charged into batteries (kWh) — all periods.</summary>
        public double TotalBatteryChargedKWh { get; set; }

        /// <summary>Total energy discharged from batteries (kWh) — all periods.</summary>
        public double TotalBatteryDischargedKWh { get; set; }

        /// <summary>
        /// Energy charged over reliable periods only (kWh).
        /// Used for round-trip efficiency to exclude periods with known data quality issues
        /// (e.g. battery overheating causing premature shutdown mid-cycle).
        /// </summary>
        public double ReliableBatteryChargedKWh { get; set; }

        /// <summary>
        /// Energy discharged over reliable periods only (kWh).
        /// Used for round-trip efficiency to exclude periods with known data quality issues.
        /// </summary>
        public double ReliableBatteryDischargedKWh { get; set; }

        /// <summary>Number of full equivalent battery cycles in the measured period.</summary>
        public double BatteryCycles { get; set; }

        /// <summary>Average battery cycles per day, based on measured days (not period length).</summary>
        public double BatteryCyclesPerDay { get; set; }

        /// <summary>
        /// Battery round-trip efficiency (discharged / charged).
        /// Calculated from reliable periods only to exclude overheating/shutdown distortion.
        /// Only meaningful when at least 1 kWh has been charged AND discharged.
        /// Returns 0 when insufficient data to avoid misleading values.
        /// </summary>
        public double BatteryRoundTripEfficiencyPct =>
            ReliableBatteryChargedKWh >= 1.0 && ReliableBatteryDischargedKWh > 0
                ? Math.Min(100.0, ReliableBatteryDischargedKWh / ReliableBatteryChargedKWh * 100.0)
                : 0.0;

        /// <summary>Average state of charge percentage.</summary>
        public double AverageSocPct { get; set; }

        // ── Financial statistics ─────────────────────────────────────────────

        /// <summary>Actual energy cost paid (EUR).</summary>
        public double ActualEnergyCostEur { get; set; }

        /// <summary>
        /// Estimated energy cost without solar/battery — based on same consumption
        /// at average grid price (EUR).
        /// </summary>
        public double BaselineEnergyCostEur { get; set; }

        /// <summary>Total savings vs baseline (EUR).</summary>
        public double TotalSavingsEur => BaselineEnergyCostEur - ActualEnergyCostEur;

        /// <summary>Revenue from grid export (EUR).</summary>
        public double GridExportRevenueEur { get; set; }

        /// <summary>Battery arbitrage profit (buy low, sell high) (EUR).</summary>
        public double ArbitrageProfitEur { get; set; }

        /// <summary>Weighted average buy price per kWh (EUR/kWh).</summary>
        public double WeightedAvgBuyPriceEurPerKWh { get; set; }

        /// <summary>Weighted average sell price per kWh (EUR/kWh).</summary>
        public double WeightedAvgSellPriceEurPerKWh { get; set; }

        /// <summary>Monthly savings extrapolated from period (EUR/month).</summary>
        public double MonthlySavingsEur => PeriodDays > 0 ? TotalSavingsEur / PeriodDays * 30.0 : 0.0;

        /// <summary>Annual savings extrapolated from period (EUR/year).</summary>
        public double AnnualSavingsEur => PeriodDays > 0 ? TotalSavingsEur / PeriodDays * 365.0 : 0.0;

        // ── Solar statistics ─────────────────────────────────────────────────

        /// <summary>Average daily solar production (kWh/day).</summary>
        public double AvgDailySolarProductionKWh => PeriodDays > 0 ? TotalSolarProductionKWh / PeriodDays : 0.0;

        /// <summary>Peak solar production in a single day (kWh).</summary>
        public double PeakDailySolarProductionKWh { get; set; }

        /// <summary>Solar performance ratio (actual vs expected based on radiation).</summary>
        public double SolarPerformanceRatio { get; set; }

        // ── Consumption statistics ───────────────────────────────────────────

        /// <summary>Average daily consumption (kWh/day).</summary>
        public double AvgDailyConsumptionKWh => PeriodDays > 0 ? TotalConsumptionKWh / PeriodDays : 0.0;

        /// <summary>Peak daily consumption (kWh).</summary>
        public double PeakDailyConsumptionKWh { get; set; }

        /// <summary>Weekday vs weekend consumption ratio.</summary>
        public double WeekdayConsumptionKWh { get; set; }
        public double WeekendConsumptionKWh { get; set; }

        // ── Period helpers ───────────────────────────────────────────────────

        /// <summary>
        /// The actual start of the data used in this statistics object.
        /// Set by EnergyStatisticsService after querying the data.
        /// Used to calculate PeriodDays correctly when PeriodStart is DateTime.MinValue.
        /// </summary>
        public DateTime ActualDataStart { get; set; }

        /// <summary>
        /// The actual end of the data used in this statistics object.
        /// Set by EnergyStatisticsService after querying the data.
        /// Used to calculate PeriodDays correctly when PeriodEnd is DateTime.MaxValue.
        /// </summary>
        public DateTime ActualDataEnd { get; set; }

        /// <summary>
        /// Number of days in the period, based on actual data range.
        /// Falls back to PeriodStart/PeriodEnd when ActualDataStart/End are not set.
        /// Prevents division by astronomical values when DateTime.MinValue/MaxValue are used.
        /// </summary>
        public double PeriodDays
        {
            get
            {
                var start = ActualDataStart != default ? ActualDataStart : PeriodStart;
                var end = ActualDataEnd != default ? ActualDataEnd : PeriodEnd;

                // Guard against MinValue/MaxValue being passed through.
                if (start == DateTime.MinValue || end == DateTime.MaxValue)
                    return 0.0;

                return Math.Max(1.0, (end - start).TotalDays);
            }
        }
    }

    /// <summary>
    /// ROI and payback period statistics.
    /// </summary>
    public class InvestmentStatistics
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }

        /// <summary>Total net investment (after subsidies) in EUR.</summary>
        public double TotalNetInvestmentEur { get; set; }

        /// <summary>
        /// Realized savings based on available data period.
        /// </summary>
        public double TotalRealizedSavingsEur { get; set; }

        /// <summary>
        /// Projected total savings extrapolated back to earliest installation date,
        /// using seasonal monthly averages from available data.
        /// </summary>
        public double ProjectedTotalSavingsEur { get; set; }

        /// <summary>
        /// Projected savings used for ROI — uses ProjectedTotalSavings when
        /// data period is shorter than installation period.
        /// </summary>
        public double EffectiveTotalSavingsEur =>
            ProjectedTotalSavingsEur > TotalRealizedSavingsEur
                ? ProjectedTotalSavingsEur
                : TotalRealizedSavingsEur;

        /// <summary>Remaining investment to recover (EUR).</summary>
        public double RemainingInvestmentEur => Math.Max(0, TotalNetInvestmentEur - EffectiveTotalSavingsEur);

        /// <summary>Percentage of investment recovered so far.</summary>
        public double RecoveredPct => TotalNetInvestmentEur > 0
            ? Math.Min(100.0, EffectiveTotalSavingsEur / TotalNetInvestmentEur * 100.0)
            : 0.0;

        /// <summary>Projected annual savings based on recent trend (EUR/year).</summary>
        public double ProjectedAnnualSavingsEur { get; set; }

        /// <summary>Projected payback period in years based on current savings rate.</summary>
        public double ProjectedPaybackYears => ProjectedAnnualSavingsEur > 0
            ? TotalNetInvestmentEur / ProjectedAnnualSavingsEur
            : double.MaxValue;

        /// <summary>Projected break-even date.</summary>
        public DateTime? ProjectedBreakEvenDate { get; set; }

        /// <summary>
        /// Start date of the available data used for projections.
        /// </summary>
        public DateTime DataAvailableFrom { get; set; }

        /// <summary>
        /// True when the data period is shorter than the installation period,
        /// meaning projections are used instead of measured values.
        /// </summary>
        public bool UsesProjection => DataAvailableFrom > PeriodStart;

        /// <summary>Per-category investment breakdown.</summary>
        public List<InvestmentCategoryStats> CategoryBreakdown { get; set; } = new();

        /// <summary>
        /// Per-investment breakdown — each investment listed individually regardless of group.
        /// Used for the detail table in the UI.
        /// </summary>
        public List<InvestmentCategoryStats> ComponentBreakdown { get; set; } = new();
    }

    /// <summary>
    /// Statistics per investment category including per-component savings calculation.
    /// </summary>
    public class InvestmentCategoryStats
    {
        public string Category { get; set; } = string.Empty;
        public double TotalAmountEur { get; set; }
        public double TotalSubsidyEur { get; set; }
        public double NetAmountEur => TotalAmountEur - TotalSubsidyEur;
        public int ExpectedLifetimeYears { get; set; }
        public double AnnualDepreciationEur => ExpectedLifetimeYears > 0 ? NetAmountEur / ExpectedLifetimeYears : 0.0;

        /// <summary>Installation date of the earliest component in this category.</summary>
        public DateTime InstallationDate { get; set; }

        /// <summary>Number of months since installation.</summary>
        public double MonthsSinceInstallation { get; set; }

        /// <summary>Calculated or estimated annual savings for this category (EUR/year).</summary>
        public double AnnualSavingsEur { get; set; }

        /// <summary>Monthly savings (EUR/month).</summary>
        public double MonthlySavingsEur => AnnualSavingsEur / 12.0;

        /// <summary>Total projected savings since installation (EUR).</summary>
        public double ProjectedTotalSavingsEur => MonthlySavingsEur * MonthsSinceInstallation;

        /// <summary>Simple payback period in years.</summary>
        public double PaybackYears => AnnualSavingsEur > 0
            ? NetAmountEur / AnnualSavingsEur
            : double.MaxValue;

        /// <summary>Projected break-even date for this component.</summary>
        public DateTime? BreakEvenDate => AnnualSavingsEur > 0
            ? InstallationDate.AddDays(PaybackYears * 365.0)
            : null;

        /// <summary>Percentage of investment recovered so far.</summary>
        public double RecoveredPct => NetAmountEur > 0
            ? Math.Min(100.0, ProjectedTotalSavingsEur / NetAmountEur * 100.0)
            : 0.0;

        /// <summary>Description of how savings are calculated.</summary>
        public string SavingsSource { get; set; } = string.Empty;
        public double BatteryCycles { get; internal set; }
    }

    /// <summary>
    /// Monthly trend entry for visualization.
    /// </summary>
    public class MonthlyTrend
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public DateTime PeriodStart => new DateTime(Year, Month, 1);
        public double SolarProductionKWh { get; set; }
        public double ConsumptionKWh { get; set; }
        public double GridImportKWh { get; set; }
        public double GridExportKWh { get; set; }
        public double SavingsEur { get; set; }
        public double ArbitrageProfitEur { get; set; }
        public double SelfSufficiencyPct { get; set; }
        public double BatteryCycles { get; set; }
    }
}