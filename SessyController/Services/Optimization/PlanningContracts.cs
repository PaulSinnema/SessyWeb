using SessyController.Services.Items;

namespace SessyController.Services.Optimization
{
    /// <summary>What the battery is doing during a quarter.</summary>
    public enum ActionMode
    {
        Idle = 0,
        /// <summary>Actively charging from the grid.</summary>
        Charge = 1,
        /// <summary>Actively discharging to the grid (export).</summary>
        Discharge = 2,
        /// <summary>Self-regulating: storing solar surplus and/or covering the house load.</summary>
        ZeroNetHome = 3
    }

    /// <summary>
    /// Physical properties and limits of the battery bank.
    /// ChargeTaper models the CC/CV fall-off of charge power with rising SOC; null means no
    /// taper (nameplate power at any SOC). DischargeCapability does the same on the way out,
    /// where the shape is a plateau with a knee rather than a slope; null means nameplate.
    /// </summary>
    public sealed record BatterySpec(
        double CapacityKWh,
        double InitialSocKWh,
        double MaxChargeKW,
        double MaxDischargeKW,
        double ChargeEfficiency,
        double DischargeEfficiency,
        ChargeTaper? ChargeTaper = null,
        EfficiencyCurve? Efficiency = null,
        DischargeCapability? DischargeCapability = null,
        ChargeCapabilityFloor? ChargeFloor = null
    );

    /// <summary>Planner tuning.</summary>
    /// <param name="QuarterMinutes">Length of one planning slot in minutes (15).</param>
    /// <param name="CycleCostEurPerKWh">Battery wear per kWh discharged, derived from the investments.</param>
    /// <param name="AllowExport">
    /// When false the battery may never push energy to the grid; it only stores solar and covers
    /// the house load. Used by the self-consumption strategy.
    /// </param>
    /// <param name="FutureValueDiscountPerHour">
    /// Continuous per-hour discount applied to value[j] and cost[i] inside the arbitrage
    /// comparison — a quarter h hours from now is worth 1 / (1 + FutureValueDiscountPerHour * h)
    /// of its real price when the planner decides where to send stock. Reflects genuine forecast
    /// uncertainty growing with distance, so a farther peak needs to be proportionally better to
    /// still win over a nearer one, instead of winner-takes-all always claiming the single best
    /// quarter in the whole horizon. The reported objective and executed plan still use full,
    /// undiscounted prices — this only shapes which comparable quarter the search reaches for.
    /// 0 (default) reproduces the original undiscounted behaviour exactly.
    /// </param>
    /// <param name="ReplacementCostEurPerKWh">
    /// What one kWh will cost to put back into the battery later (EUR/kWh, AC side), measured
    /// by ReplacementCostService. It gives energy a value outside the horizon, which the
    /// objective otherwise puts at zero, and it is used on both sides of that:
    ///
    ///   as a floor      Stock is not sold below what buying it back would cost. Note this is a
    ///                   reservation price, never a cost to be recovered — what was actually
    ///                   paid is sunk and must not decide when to sell.
    ///   as a value      Charging for beyond the end of the horizon becomes possible at all
    ///                   (see AllowCarryForward), which is where charging at a negative price
    ///                   and simply holding pays.
    ///
    /// 0 (default) restores the original behaviour exactly.
    /// </param>
    /// <param name="AllowCarryForward">
    /// Whether the planner may charge purely to carry energy past the end of the horizon, valued
    /// at ReplacementCostEurPerKWh. Off (default) keeps every charge tied to a discharge quarter
    /// inside the horizon, as before.
    /// </param>
    public sealed record SessyOptions(
        int QuarterMinutes,
        double CycleCostEurPerKWh,
        bool AllowExport = true,
        double FutureValueDiscountPerHour = 0.0,
        double ReplacementCostEurPerKWh = 0.0,
        bool AllowCarryForward = false
    );

    /// <summary>Allowed state-of-charge window at the end of a quarter.</summary>
    public sealed record SocBound(DateTime Time, double MinSocKWh, double MaxSocKWh);

    /// <summary>
    /// Input for one quarter.
    /// NetLoadWh is the household load minus solar, in Wh: positive means the house needs grid
    /// power, negative means there is a solar surplus.
    /// MaxChargeKW / MaxDischargeKW override the battery spec when throttling applies.
    /// ReserveOnly marks a quarter whose price is predicted rather than published: the planner
    /// may use it to reserve energy for the coming night, but must not trade on it.
    /// TemperatureC and Temperature48hC feed the charge taper; null means "unknown", and the
    /// taper then falls back to its reference temperature.
    /// </summary>
    public sealed record PricePoint(
        DateTime Start,
        double BuyEurPerKWh,
        double SellEurPerKWh,
        double NetLoadWh,
        double SolarSurplusWh,
        double? MaxChargeKW = null,
        double? MaxDischargeKW = null,
        bool ReserveOnly = false,
        double? TemperatureC = null,
        double? Temperature48hC = null
    );

    /// <summary>
    /// The planned action for one quarter.
    /// RequestedDischargeKW is the same idea as RequestedChargeKW on the way out: DischargeKW is
    /// what the plan expects to leave the battery, the request is what the batteries are asked
    /// for, which stays at nameplate wherever the measured capability was the binding cap.
    /// ChargeKW is what the plan EXPECTS to arrive, taper included — it drives the SOC path.
    /// RequestedChargeKW is what the batteries should be ASKED for: the same number, except in
    /// quarters where the taper was the binding cap, where it is the untapered limit. The
    /// batteries throttle themselves; asking the tapered value only made the throttle fit measure
    /// its own request.
    /// </summary>
    public sealed record PlanStep(
        DateTime Start,
        ActionMode Mode,
        double ChargeKW,
        double DischargeKW,
        double SocStartKWh,
        double SocEndKWh,
        double RequestedChargeKW = 0.0,
        double RequestedDischargeKW = 0.0
    );

    /// <summary>
    /// Result of a planning run. <paramref name="Optimal"/> is kept for compatibility with the
    /// callers; the greedy planner always produces a plan, so it is always true.
    /// </summary>
    public sealed record PlanResult(bool Optimal, double ObjectiveEur, IReadOnlyList<PlanStep> Plan);
}