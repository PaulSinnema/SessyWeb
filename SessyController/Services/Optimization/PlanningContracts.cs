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

    /// <summary>Physical properties and limits of the battery bank.</summary>
    public sealed record BatterySpec(
        double CapacityKWh,
        double InitialSocKWh,
        double MaxChargeKW,
        double MaxDischargeKW,
        double ChargeEfficiency,
        double DischargeEfficiency
    );

    /// <summary>Planner tuning.</summary>
    /// <param name="QuarterMinutes">Length of one planning slot in minutes (15).</param>
    /// <param name="CycleCostEurPerKWh">Battery wear per kWh discharged, derived from the investments.</param>
    /// <param name="AllowExport">
    /// When false the battery may never push energy to the grid; it only stores solar and covers
    /// the house load. Used by the self-consumption strategy.
    /// </param>
    public sealed record SessyOptions(
        int QuarterMinutes,
        double CycleCostEurPerKWh,
        bool AllowExport = true
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
    /// </summary>
    public sealed record PricePoint(
        DateTime Start,
        double BuyEurPerKWh,
        double SellEurPerKWh,
        double NetLoadWh,
        double SolarSurplusWh,
        double? MaxChargeKW = null,
        double? MaxDischargeKW = null,
        bool ReserveOnly = false
    );

    /// <summary>The planned action for one quarter.</summary>
    public sealed record PlanStep(
        DateTime Start,
        ActionMode Mode,
        double ChargeKW,
        double DischargeKW,
        double SocStartKWh,
        double SocEndKWh
    );

    /// <summary>
    /// Result of a planning run. <paramref name="Optimal"/> is kept for compatibility with the
    /// callers; the greedy planner always produces a plan, so it is always true.
    /// </summary>
    public sealed record PlanResult(bool Optimal, double ObjectiveEur, IReadOnlyList<PlanStep> Plan);
}