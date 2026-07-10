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

        public bool WeAreInControl => !(ChargedInControl || ManualOverride);

        [SkipCopy]
        public string? ManualChargingHours { get; set; }

        [SkipCopy]
        public string? ManualDischargingHours { get; set; }

        [SkipCopy]
        public string? ManualNetZeroHomeHours { get; set; }

        // ── Legacy — kept for backwards compatibility ──────────────────────
        [SkipCopy]
        public string? Hours { get; set; }

        // ── General ───────────────────────────────────────────────────────────
        public string? TimeZone { get; set; }

        // ── Solar ─────────────────────────────────────────────────────────────
        public double SolarAnnualProductionKWh { get; set; }
        public bool SolarSystemShutsDownDuringNegativePrices { get; set; }

        // ── Battery planning ─────────────────────────────────────────────────
        /// <summary>
        /// Night reserve cap as a percentage of total capacity (0-100).
        /// When 0, defaults to 33%. Limits how much energy is held back for nightly consumption.
        /// </summary>
        public double NightReserveCapPct { get; set; }

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