using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// Single source of truth for all quarter-hour measurements.
    /// Replaces the old Performance + EnergyHistory split.
    ///
    /// Unit conventions:
    ///   BatteryPowerWatts        — Watts; negative = charging, positive = discharging
    ///   BatteryStateOfChargeWh   — Wh
    ///   SolarProductionKWh       — kWh per quarter-hour (measured by inverter)
    ///   GridImportWh             — Wh imported from grid this quarter (P1 delta)
    ///   GridExportWh             — Wh exported to grid this quarter (P1 delta)
    ///   BuyingPriceEur           — EUR/kWh (incl. taxes)
    ///   SellingPriceEur          — EUR/kWh (incl. taxes)
    ///   GlobalRadiation          — W/m² (KNMI)
    /// </summary>
    [Index(nameof(Time))]
    public class QuarterlyMeasurement
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Quarter-hour start timestamp (local time).</summary>
        public DateTime Time { get; set; }

        // ── Battery ───────────────────────────────────────────────────────────

        /// <summary>
        /// Measured battery power in Watts (Sessy API).
        /// Negative = charging (energy into battery).
        /// Positive = discharging (energy out of battery).
        /// Zero = idle.
        /// </summary>
        public double BatteryPowerWatts { get; set; }

        /// <summary>Measured state of charge in Wh (Sessy API).</summary>
        public double BatteryStateOfChargeWh { get; set; }

        /// <summary>
        /// Battery operating mode for this quarter.
        /// Matches the mode commanded by MilpService.
        /// </summary>
        public BatteryMode BatteryMode { get; set; }

        /// <summary>
        /// Whether battery data for this record is reliable.
        /// Set to false for periods with known data quality issues
        /// (e.g. overheating causing premature discharge cutoff).
        /// Round-trip efficiency is only calculated over reliable records.
        /// </summary>
        public bool IsReliable { get; set; } = true;

        // ── Solar ─────────────────────────────────────────────────────────────

        /// <summary>Solar energy produced this quarter in kWh (inverter measurement).</summary>
        public double SolarProductionKWh { get; set; }

        // ── Grid (P1 meter deltas) ────────────────────────────────────────────

        /// <summary>Energy imported from grid this quarter in Wh (P1 delta).</summary>
        public double GridImportWh { get; set; }

        /// <summary>Energy exported to grid this quarter in Wh (P1 delta).</summary>
        public double GridExportWh { get; set; }

        // ── Prices ────────────────────────────────────────────────────────────

        /// <summary>Effective buying price this quarter in EUR/kWh (incl. taxes).</summary>
        public double BuyingPriceEur { get; set; }

        /// <summary>Effective selling price this quarter in EUR/kWh (incl. taxes).</summary>
        public double SellingPriceEur { get; set; }

        // ── Weather ───────────────────────────────────────────────────────────

        /// <summary>Global solar radiation in W/m² (KNMI).</summary>
        public double GlobalRadiation { get; set; }

        // ── Derived helpers (not stored) ──────────────────────────────────────

        /// <summary>Solar energy produced this quarter in kWh (convenience alias).</summary>
        [NotMapped]
        public double SolarProductionWh => SolarProductionKWh * 1000.0;

        /// <summary>Grid import in kWh.</summary>
        [NotMapped]
        public double GridImportKWh => GridImportWh / 1000.0;

        /// <summary>Grid export in kWh.</summary>
        [NotMapped]
        public double GridExportKWh => GridExportWh / 1000.0;

        /// <summary>Battery charged energy this quarter in kWh.</summary>
        [NotMapped]
        public double BatteryChargedKWh => BatteryPowerWatts < 0
            ? Math.Abs(BatteryPowerWatts) * 0.25 / 1000.0
            : 0.0;

        /// <summary>Battery discharged energy this quarter in kWh.</summary>
        [NotMapped]
        public double BatteryDischargedKWh => BatteryPowerWatts > 0
            ? BatteryPowerWatts * 0.25 / 1000.0
            : 0.0;

        public override string ToString() =>
            $"{Time:yyyy-MM-dd HH:mm} | Mode={BatteryMode} | " +
            $"Battery={BatteryPowerWatts:F0}W SOC={BatteryStateOfChargeWh:F0}Wh | " +
            $"Solar={SolarProductionKWh:F4}kWh | " +
            $"Import={GridImportWh:F0}Wh Export={GridExportWh:F0}Wh | " +
            $"Buy={BuyingPriceEur:F4} Sell={SellingPriceEur:F4}";
    }

    /// <summary>Battery operating mode for a quarter-hour.</summary>
    public enum BatteryMode
    {
        /// <summary>Battery idle, no grid interaction.</summary>
        Disabled = 0,

        /// <summary>Actively charging from grid.</summary>
        Charging = 1,

        /// <summary>Actively discharging to grid or household.</summary>
        Discharging = 2,

        /// <summary>Zero net home — balancing household load.</summary>
        ZeroNetHome = 3
    }
}