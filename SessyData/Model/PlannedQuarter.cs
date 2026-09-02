using Microsoft.EntityFrameworkCore;
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
    [Index(nameof(Time))]
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
        /// Planned charge power (W) exactly as the planner produced it — 0 when this quarter does
        /// not charge. Stored separately from PlannedDischargePowerW so the view no longer has to
        /// reconstruct direction from PlannedMode, which only knew Charging/Discharging and dropped
        /// every ZeroNetHome/Disabled quarter to 0.
        /// </summary>
        public double PlannedChargePowerW { get; set; }

        /// <summary>
        /// Planned discharge power (W) exactly as the planner produced it — 0 when this quarter
        /// does not discharge. Includes ZeroNetHome self-consumption discharge, which the old
        /// mode-string reconstruction lost.
        /// </summary>
        public double PlannedDischargePowerW { get; set; }

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

        /// <summary>
        /// Projected FIFO cost basis of the battery contents at this quarter, in EUR per
        /// *stored* kWh. Written from QuarterlyInfo.ProjectedCostBasisEur. Divide by the
        /// discharge efficiency for the cost per delivered kWh. Kept so projected and realized
        /// cost basis can be compared after the fact.
        /// </summary>
        public double ProjectedCostBasisEurKWh { get; set; }

        // ── Forecast ───────────────────────────────────────────────────────────

        /// <summary>
        /// Solar production forecast for this quarter. Despite the name this is **Wh for the
        /// quarter**, not Watts: it is written from QuarterlyInfo.SolarPowerPerQuarterInWatts,
        /// which is SolarPowerPerQuarterHour × 1000. ConsumptionForecastW next to it really is
        /// Watts. Use NetLoadWh below rather than combining these two by hand.
        /// </summary>
        public double SolarForecastW { get; set; }

        /// <summary>Estimated consumption for this quarter (W — average power).</summary>
        public double ConsumptionForecastW { get; set; }

        /// <summary>
        /// Household load minus solar for this quarter (Wh), exactly as the planner received it.
        /// Stored because the two forecast columns above are in different units, so anyone
        /// reconstructing this afterwards gets it wrong — which is what happened.
        /// </summary>
        public double NetLoadWh { get; set; }

        /// <summary>
        /// The reserve floor the planner had to stay above at this quarter (Wh) — the SOC bound
        /// from MilpServiceBase.ComputeSocBounds. Not derivable from anything else in the
        /// database, and it decides how much the planner is allowed to sell.
        /// </summary>
        public double MinSocWh { get; set; }

        public void Update(PlannedQuarter updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}