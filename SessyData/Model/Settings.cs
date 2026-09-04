using SessyCommon.Attributes;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// Controls how the battery system optimises its charge/discharge behaviour.
    /// </summary>
    public enum OptimizationStrategy
    {
        /// <summary>Maximise profit: charge cheap, discharge expensive, ignore solar headroom.</summary>
        ProfitMaximization = 0,

        /// <summary>Maximise self-consumption: reserve battery headroom for solar, minimise export.</summary>
        SelfConsumption = 1,

        /// <summary>Balanced: profit-first but always reserve headroom for solar surplus.</summary>
        Balanced = 2,

        /// <summary>Battery-saving: only charge/discharge at large price spreads to reduce cycle count.</summary>
        BatterySaving = 3,
    }

    /// <summary>
    /// Controls whether predicted (not-yet-published) EPEX prices are used by the MILP solver.
    /// </summary>
    public enum PredictedPriceMode
    {
        /// <summary>Off: only known/published prices enter the solver (safest, short horizon).</summary>
        Off = 0,

        /// <summary>
        /// Soft: predicted quarters extend the horizon, but their prices carry a risk margin
        /// (predicted buy raised, predicted sell lowered) so arbitrage only happens on ample spread.
        /// </summary>
        SoftMargin = 1,

        /// <summary>Full: predicted prices are trusted as-is, exactly like known prices.</summary>
        Full = 2,
    }

    public class Settings : IUpdatable<Settings>
    {
        [Key]
        public int Id { get; set; }

        // ── Location ──────────────────────────────────────────────────────────
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        // ── Control ───────────────────────────────────────────────────────────
        public bool ChargedInControl { get; set; }
        public bool ManualOverride { get; set; }

        [SkipCopy]
        public string? ManualChargingHours { get; set; }

        [SkipCopy]
        public string? ManualDischargingHours { get; set; }

        [SkipCopy]
        public string? ManualNetZeroHomeHours { get; set; }

        // ── General ───────────────────────────────────────────────────────────
        public string? TimeZone { get; set; }

        // ── Solar ─────────────────────────────────────────────────────────────
        public double SolarAnnualProductionKWh { get; set; }

        // ── Battery planning ─────────────────────────────────────────────────
        /// <summary>
        /// Night reserve cap as a percentage of total capacity (0-100).
        /// When 0, defaults to 33%. Limits how much energy is held back for nightly consumption.
        /// </summary>
        public double NightReserveCapPct { get; set; }

        /// <summary>
        /// When true (default), the night reserve comes from the self-learned NightReserveCapPct
        /// (historical forecast errors). When false, the planner uses the fixed FixedNightReservePct
        /// below, giving direct manual control over the reserve.
        /// </summary>
        public bool UseCalculatedNightReserve { get; set; } = true;

        /// <summary>
        /// Fixed night reserve as a percentage of total capacity (0-100), used only when
        /// UseCalculatedNightReserve is false. Default 10%.
        /// </summary>
        public double FixedNightReservePct { get; set; } = 10.0;

        /// <summary>
        /// When true (default), the cycle (wear) cost is derived from the battery investments
        /// (Σ net cost / Σ capacity·cycles). When false, the planner uses the fixed
        /// FixedCycleCostEurPerKWh below — set it to 0 to disable the wear cost entirely.
        /// </summary>
        public bool UseCalculatedCycleCost { get; set; } = false;

        /// <summary>
        /// Fixed cycle (wear) cost in EUR/kWh, used only when UseCalculatedCycleCost is false.
        /// Default 0 = no wear cost, which lets the planner trade on every profitable spread.
        /// </summary>
        public double FixedCycleCostEurPerKWh { get; set; } = 0.04;

        /// <summary>
        /// Fallback for the throttle ratio (%), used only while ThrottleAnalysisService has no
        /// measured samples for the current temperature. The throttle ratio caps how much *power*
        /// the planner may request; once samples exist the measured ratio takes over.
        /// When 0, defaults to 80%.
        /// </summary>
        public double ThrottleFallbackPct { get; set; }

        /// <summary>
        /// Fallback for the battery round-trip *energy* efficiency (%), used while there is not
        /// enough measured data. The planner derives the one-way charge and discharge efficiency
        /// from this as sqrt(roundTrip). When 0, defaults to 90%.
        /// </summary>
        public double RoundTripEfficiencyFallbackPct { get; set; }

        /// <summary>
        /// Continuous per-hour discount the greedy planner applies to future prices when deciding
        /// where to send stored energy: a quarter h hours away is worth 1 / (1 + rate * h) of its
        /// real price in that comparison only — the reported plan and objective still use full,
        /// undiscounted prices. Replaces the old near-term-hedge pass (removed): rather than a
        /// discrete rule that reserves stock for a hand-picked nearby quarter, every quarter is
        /// compared on equal footing with a smooth time preference, so a distant peak must be
        /// genuinely better by more than the accumulated forecast-uncertainty discount to still
        /// win, instead of a single far-off peak always being able to claim the entire battery.
        /// 0.003 (default) means a peak 24h out needs to be roughly 7% better to still win over
        /// an equally-reachable one tonight. 0 disables this (original undiscounted behaviour).
        ///
        /// The default comes from measured plan stability, not a guess: over the PlannedActions
        /// history a planned mode still matches the plan that actually ran in 94-97% of cases up
        /// to 18h ahead, which implies 0.002-0.006 per hour. Beyond 24h stability collapses (23%
        /// at 30-36h), but with PredictedPriceMode.Off those quarters are reserve-only and are not
        /// traded anyway. Raise this again if predicted prices are ever opened up for trading —
        /// the two settings belong together.
        ///
        /// Note the UI shows this as a PERCENTAGE per hour (SettingsPage divides by 100), so the
        /// default reads as 0.3 there.
        /// </summary>
        public double FutureValueDiscountPerHour { get; set; } = 0.003;

        // ── Carry-forward / replacement cost ──────────────────────────────────

        /// <summary>
        /// Whether the planner may charge purely to hold energy past the end of its horizon,
        /// valued at the measured replacement cost. Off means every charge must be paired with a
        /// discharge quarter inside the horizon, which leaves the planner blind exactly where
        /// holding pays: cheap or negative prices whose payoff lies further out than it can see.
        /// Off by default: it changes what the planner buys, so it is switched on deliberately.
        /// </summary>
        public bool CarryForwardEnabled { get; set; }

        /// <summary>
        /// Trailing window (days) over which the replacement cost is measured. 0 = default (30).
        /// </summary>
        public int ReplacementCostWindowDays { get; set; } = 30;

        /// <summary>
        /// Percentile of the daily cheapest buy price that becomes the replacement cost. Low on
        /// purpose: too high a value makes charging always attractive and selling never, and the
        /// battery ends up full and idle. 0 = default (25).
        /// </summary>
        public double ReplacementCostPercentile { get; set; } = 25.0;

        // ── Self-learning planner parameters ──────────────────────────────────

        /// <summary>
        /// Whether the nightly learner derives <see cref="FutureValueDiscountPerHour"/> and
        /// <see cref="NightReserveCapPct"/> from measured forecast errors and overwrites them.
        /// While there is not enough history it writes nothing and both settings keep their
        /// configured values, so they can still be tuned by hand in the meantime.
        /// Off by default: it overwrites values you may have tuned.
        /// </summary>
        public bool SelfLearningEnabled { get; set; }

        /// <summary>When the learner last wrote its values. Null = never.</summary>
        public DateTime? LastLearnedAt { get; set; }

        /// <summary>What the learner concluded last time, for the UI and the Tips tab.</summary>
        public string? LastLearnedSummary { get; set; }

        // ── User interface ────────────────────────────────────────────────────

        /// <summary>
        /// Whether the navigation menu stays expanded after a menu item is clicked. Off restores
        /// the original behaviour, where every click collapsed it back to icons. On a phone the
        /// expanded menu covers part of the page, which is why this is a choice and not a fix.
        /// </summary>
        public bool KeepMenuExpanded { get; set; } = true;

        // ── Statistics ────────────────────────────────────────────────────────
        public DateTime? StatisticsFromDate { get; set; }

        /// <summary>
        /// Kept for DB column compatibility. Backup directory is configured
        /// via appsettings.json (SettingsConfig), not via this property.
        /// </summary>
        public string? DatabaseBackupDirectory { get; set; }

        // ── Export ────────────────────────────────────────────────────────────
        public string? ExportDirectory { get; set; }

        // ── Energy needs ──────────────────────────────────────────────────────
        [SkipCopy]
        public string? RequiredHomeEnergy { get; set; }

        // ── NotMapped helpers ─────────────────────────────────────────────────

        [NotMapped]
        public int[] ManualChargingHoursArray
        {
            get => ManualChargingHours.StringToArray<int>();
            set => ManualChargingHours = value.StringFromArray<int>();
        }

        [NotMapped]
        public int[] ManualDischargingHoursArray
        {
            get => ManualDischargingHours.StringToArray<int>();
            set => ManualDischargingHours = value.StringFromArray<int>();
        }

        [NotMapped]
        public int[] ManualNetZeroHomeHoursArray
        {
            get => ManualNetZeroHomeHours.StringToArray<int>();
            set => ManualNetZeroHomeHours = value.StringFromArray<int>();
        }

        [NotMapped]
        public double[] RequiredHomeEnergyArray
        {
            get => RequiredHomeEnergy.StringToArray<double>();
            set => RequiredHomeEnergy = value.StringFromArray<double>();
        }

        /// <summary>Returns the estimated home energy need for the current month in Wh/day.</summary>
        public double EnergyNeedsForCurrentMonth(int monthIndex)
        {
            var arr = RequiredHomeEnergyArray;
            if (arr == null || arr.Length == 0) return 0.0;
            int idx = Math.Clamp(monthIndex, 0, arr.Length - 1);
            return arr[idx];
        }

        /// <summary>Optimisation strategy for the battery system.</summary>
        public OptimizationStrategy Strategy { get; set; } = OptimizationStrategy.Balanced;

        // ── MILP tuning parameters ────────────────────────────────────────────

        /// <summary>
        /// Safety margin applied on top of the calculated night/bridge reserve.
        /// 1.10 = keep 10% extra. Increase if the battery regularly runs empty overnight.
        /// </summary>
        public double ReserveSafetyFactor { get; set; } = 1.10;

        /// <summary>
        /// Maximum planning horizon in hours. The solver ignores quarters beyond this
        /// window so it cannot defer discharge to a far-future peak. 0 = no limit
        /// (use all quarters with known prices). Typical values: 24 or 36.
        /// </summary>
        public int PlanningHorizonHours { get; set; } = 0;

        /// <summary>
        /// Whether the solver uses predicted (not-yet-published) EPEX prices. See
        /// <see cref="PredictedPriceMode"/>. Off = known prices only.
        /// </summary>
        public PredictedPriceMode PredictedPriceMode { get; set; } = PredictedPriceMode.Off;

        /// <summary>
        /// Risk margin (EUR/kWh) applied to predicted prices in SoftMargin mode: predicted
        /// buy prices are raised and predicted sell prices lowered by this amount, so the
        /// solver only arbitrages predicted quarters when the spread is comfortably large.
        /// </summary>
        public double PredictedPriceRiskMarginEur { get; set; } = 0.05;

        public void Update(Settings updateInfo)
        {
            this.Copy(updateInfo);

            ManualChargingHoursArray = updateInfo.ManualChargingHoursArray;
            ManualDischargingHoursArray = updateInfo.ManualDischargingHoursArray;
            ManualNetZeroHomeHoursArray = updateInfo.ManualNetZeroHomeHoursArray;
            RequiredHomeEnergyArray = updateInfo.RequiredHomeEnergyArray;
        }
    }
}