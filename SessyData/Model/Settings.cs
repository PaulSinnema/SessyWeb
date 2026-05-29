using SessyCommon.Attributes;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
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
        public double? SolarCorrection { get; set; }
        public double SolarAnnualProductionKWh { get; set; }
        public bool SolarSystemShutsDownDuringNegativePrices { get; set; }

        // ── Statistics ────────────────────────────────────────────────────────
        public DateTime? StatisticsFromDate { get; set; }

        /// <summary>
        /// Kept for DB column compatibility. Backup directory is configured
        /// via appsettings.json (SettingsConfig), not via this property.
        /// </summary>
        public string? DatabaseBackupDirectory { get; set; }

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