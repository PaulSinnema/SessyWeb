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

        // ── Legacy — kept for backwards compatibility ──────────────────────
        [SkipCopy]
        public string? Hours { get; set; }

        // ── General ───────────────────────────────────────────────────────────
        public string? TimeZone { get; set; }
        public double CycleCost { get; set; }
        public double NetZeroHomeMinProfit { get; set; }

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
        /// Override for the effective discharge efficiency factor (0.0–1.0).
        /// When 0, the per-battery DischargingEfficiencyFactor from appsettings is used.
        /// </summary>
        public double DischargingEfficiencyFactor { get; set; }

        /// <summary>
        /// Override for the effective charge efficiency factor (0.0–1.0).
        /// When 0, the per-battery ChargingEfficiencyFactor from appsettings is used.
        /// </summary>
        public double ChargingEfficiencyFactor { get; set; }

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
        /// Safety margin applied to the expected solar surplus when reserving battery
        /// headroom. 1.05 = reserve 5% more than the raw forecast surplus.
        /// Increase if the battery is often full when the sun peaks.
        /// </summary>
        public double SolarHeadroomSafetyFactor { get; set; } = 1.05;

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