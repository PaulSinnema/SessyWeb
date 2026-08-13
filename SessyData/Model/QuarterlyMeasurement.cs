using Microsoft.EntityFrameworkCore;
using SessyCommon.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// Battery telemetry per quarter-hour, and the row that says the system was running at all —
    /// QuarterlyFactsService uses it as the spine and hangs the other measured quantities off it.
    ///
    /// This is NOT the single source of truth for a whole quarter, whatever this comment used to
    /// claim: grid flows live in EnergyHistory (migration RemoveRedundancy dropped the columns
    /// documented here), solar in InverterMeasurements, household load in Consumption. Read them
    /// through QuarterlyFactsService rather than re-deriving any of them.
    ///
    /// Unit conventions:
    ///   BatteryPowerWatts        — Watts; negative = charging, positive = discharging
    ///   BatteryStateOfChargeWh   — Wh
    ///   BuyingPriceEur           — EUR/kWh (incl. taxes, computed from EPEXPrices + Taxes, not stored)
    ///   SellingPriceEur          — EUR/kWh (incl. taxes, computed from EPEXPrices + Taxes, not stored)
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

        /// <summary>
        /// Effective buying price this quarter in EUR/kWh (incl. taxes).
        /// Computed from EPEXPrices + Taxes — not stored in DB.
        /// </summary>
        [NotMapped]
        public double BuyingPriceEur { get; set; }

        /// <summary>
        /// Effective selling price this quarter in EUR/kWh (incl. taxes).
        /// Computed from EPEXPrices + Taxes — not stored in DB.
        /// </summary>
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