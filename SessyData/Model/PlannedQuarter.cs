using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SessyCommon.Extensions;

namespace SessyData.Model
{
    /// <summary>
    /// Records the MILP-planned state for each future quarter at plan build time.
    /// Written (upserted) by MilpService after every successful solve.
    ///
    /// PlannedChargeLeftWh is the stable SOC reference for deviation checks —
    /// it survives restarts and is never overwritten by actual measurements.
    ///
    /// JOIN with ActualQuarter on Time for plan vs actual comparison.
    /// </summary>
    public class PlannedQuarter : IUpdatable<PlannedQuarter>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Quarter start time (local timezone). Unique — upserted on rebuild.</summary>
        public DateTime Time { get; set; }

        // ── MILP plan ──────────────────────────────────────────────────────────

        /// <summary>Planned battery mode: Charging / Discharging / ZeroNetHome / Disabled.</summary>
        public string PlannedMode { get; set; } = string.Empty;

        /// <summary>Planned power setpoint (W), as limited by the temperature throttle.</summary>
        public double PlannedPowerW { get; set; }

        /// <summary>
        /// Planned power setpoint (W) the solver would have used without the temperature
        /// throttle. This is the throttle-free target and is the denominator for the throttle
        /// ratio, so throttling already baked into the plan does not hide future throttling.
        /// </summary>
        public double PlannedUnthrottledPowerW { get; set; }

        /// <summary>
        /// MILP-predicted SOC at the end of this quarter (Wh).
        /// Written from ChargeLeftWh after WriteBackSocSimulationAsync completes.
        /// Used as reference for SOC deviation checks after restarts.
        /// </summary>
        public double PlannedChargeLeftWh { get; set; }

        // ── Price context ──────────────────────────────────────────────────────

        /// <summary>All-in selling price for this quarter (EUR/kWh).</summary>
        public double SellingPriceEurKWh { get; set; }

        /// <summary>All-in buying price for this quarter (EUR/kWh).</summary>
        public double BuyingPriceEurKWh { get; set; }

        // ── Forecast ───────────────────────────────────────────────────────────

        /// <summary>Solar production forecast for this quarter (W).</summary>
        public double SolarForecastW { get; set; }

        /// <summary>Estimated consumption for this quarter (W).</summary>
        public double ConsumptionForecastW { get; set; }

        public void Update(PlannedQuarter updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}