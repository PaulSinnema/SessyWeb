using Microsoft.EntityFrameworkCore;
using SessyCommon.Enums;
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
    ///   GridImportWh             — Wh imported from grid this quarter (P1 delta)
    ///   GridExportWh             — Wh exported to grid this quarter (P1 delta)
    ///   BuyingPriceEur           — EUR/kWh (incl. taxes, computed from EPEXPrices + Taxes, not stored)
    ///   SellingPriceEur          — EUR/kWh (incl. taxes, computed from EPEXPrices + Taxes, not stored)
    ///   GlobalRadiation          — W/m² (KNMI)
    /// </summary>
    [Index(nameof(Time))]
    public class QuarterlyMeasurement : IUpdatable<QuarterlyMeasurement>
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
        public Modes BatteryMode { get; set; }

        /// <summary>
        /// Whether battery data for this record is reliable.
        /// Set to false for periods with known data quality issues
        /// (e.g. overheating causing premature discharge cutoff).
        /// Round-trip efficiency is only calculated over reliable records.
        /// </summary>
        public bool IsReliable { get; set; } = true;

        // ── Prices ────────────────────────────────────────────────────────────

        /// <summary>Effective buying price this quarter in EUR/kWh (incl. taxes).</summary>
        /// <summary>Computed from EPEXPrices + Taxes — not stored in DB.</summary>
        [NotMapped]
        public double BuyingPriceEur { get; set; }

        /// <summary>Effective selling price this quarter in EUR/kWh (incl. taxes).</summary>
        /// <summary>Computed from EPEXPrices + Taxes — not stored in DB.</summary>
        [NotMapped]
        public double SellingPriceEur { get; set; }

        // ── Weather ───────────────────────────────────────────────────────────

        /// <summary>
        /// Planned revenue for this quarter as calculated by the MILP (EUR).
        /// Positive = expected profit from discharge, negative = cost of charge.
        /// Compare with realized revenue to measure plan execution quality.
        /// </summary>
        public double PlannedRevenueEur { get; set; }

        // ── Derived helpers (not stored) ──────────────────────────────────────

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
            $"Buy={BuyingPriceEur:F4} Sell={SellingPriceEur:F4}";

        public void Update(QuarterlyMeasurement updateInfo)
        {
            BatteryPowerWatts = updateInfo.BatteryPowerWatts;
            BatteryStateOfChargeWh = updateInfo.BatteryStateOfChargeWh;
            BatteryMode = updateInfo.BatteryMode;
            IsReliable = updateInfo.IsReliable;
            // BuyingPriceEur and SellingPriceEur are computed — not persisted.
            PlannedRevenueEur = updateInfo.PlannedRevenueEur;
        }
    }
}