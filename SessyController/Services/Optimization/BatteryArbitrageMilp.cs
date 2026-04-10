using Google.OrTools.LinearSolver;

namespace SessyController.Services.Optimization
{
    public enum ActionMode
    {
        Idle = 0,
        Charge = 1,
        Discharge = 2
    }

    public sealed record BatterySpec(
        double CapacityKWh,
        double InitialSocKWh,
        double MinSocKWh,
        double MaxSocKWh,
        double MaxChargeKW,
        double MaxDischargeKW,
        double ChargeEfficiency,
        double DischargeEfficiency
    );

    public sealed record SessyOptions(
        int QuarterMinutes,
        double ActiveQuarterPenaltyEur,
        bool ForbidSimultaneousChargeDischarge,
        int TimeLimitMs,
        double CycleCostEurPerKWh = 0.0
    );

    /// <summary>
    /// FIX 1: Per-quarter SOC envelope for the MILP solver.
    /// The solver must keep SOC within [MinSocKWh, MaxSocKWh] at the END of each quarter.
    /// These are built from _minSocWhByTime / _maxSocWhByTime in BatteriesService
    /// and reflect solar headroom + self-use reserves.
    /// </summary>
    public sealed record SocBound(
        DateTime Time,
        double MinSocKWh,
        double MaxSocKWh
    );

    public sealed record PlanStep(
        DateTime Start,
        ActionMode Mode,
        double ChargeKW,
        double DischargeKW,
        double SocStartKWh,
        double SocEndKWh
    );

    public sealed record PlanResult(
        bool Optimal,
        double ObjectiveEur,
        IReadOnlyList<PlanStep> Plan
    );

    /// <summary>
    /// One quarter-hour price point for the MILP solver.
    /// Prices are in EUR/kWh.
    /// </summary>
    public sealed record PricePoint(
        DateTime Start,
        double BuyEurPerKWh,
        double SellEurPerKWh,
        double NetLoadWh
    );

    public static class BatteryArbitrageMilp
    {
        /// <summary>
        /// Solve the battery arbitrage MILP.
        ///
        /// FIX 1: Added optional <paramref name="socBounds"/> parameter.
        /// When provided, each quarter's SOC variable is constrained to the
        /// [MinSocKWh, MaxSocKWh] range supplied by BatteriesService, which
        /// encodes solar headroom and self-use reserves directly into the solve.
        /// This replaces the old approach of correcting the plan post-hoc via
        /// EnsureEnergyForPlannedDischargeAsync(), which produced suboptimal plans.
        /// </summary>
        public static PlanResult Solve(
            IReadOnlyList<PricePoint> pricePoints,
            BatterySpec spec,
            SessyOptions opt,
            IReadOnlyList<SocBound>? socBounds = null)
        {
            if (pricePoints == null || pricePoints.Count == 0)
                return EmptyResult();

            Solver? solver = Solver.CreateSolver("CBC_MIXED_INTEGER_PROGRAMMING");
            if (solver == null)
                return EmptyResult();

            if (opt.TimeLimitMs > 0)
                solver.SetTimeLimit(opt.TimeLimitMs);

            int n = pricePoints.Count;
            double dtHours = opt.QuarterMinutes / 60.0;

            // FIX 1: Build a per-quarter bound lookup (indexed by quarter start time).
            // Falls back to global spec bounds when no per-quarter bound is present.
            var socBoundByTime = socBounds != null
                ? socBounds.ToDictionary(b => b.Time)
                : new Dictionary<DateTime, SocBound>();

            // ----------------------------------------------------------------
            // Variables
            // ----------------------------------------------------------------

            var chargeKw = new Variable[n];
            var dischargeKw = new Variable[n];
            var isCharge = new Variable[n];
            var isDischarge = new Variable[n];

            // soc[t] = SOC at START of quarter t; soc[n] = SOC after last quarter.
            // FIX 1: Each soc[t+1] (= SOC at END of quarter t) gets its own
            // per-quarter min/max rather than the single global bound.
            // soc[0] uses the global bounds (it is the current measured SOC).
            var soc = new Variable[n + 1];

            soc[0] = solver.MakeNumVar(spec.MinSocKWh, spec.MaxSocKWh, "soc_0");

            for (int t = 0; t < n; t++)
            {
                // FIX 1: Determine per-quarter SOC bounds for the END of quarter t.
                // soc[t+1] represents the battery level after quarter t completes.
                DateTime quarterTime = pricePoints[t].Start;

                double minSoc = spec.MinSocKWh;
                double maxSoc = spec.MaxSocKWh;

                if (socBoundByTime.TryGetValue(quarterTime, out var bound))
                {
                    // Per-quarter bounds tighten (never loosen) the global bounds.
                    minSoc = Math.Max(minSoc, bound.MinSocKWh);
                    maxSoc = Math.Min(maxSoc, bound.MaxSocKWh);

                    // Guard: if the per-quarter bounds are mutually infeasible
                    // (e.g. at the planning horizon edge where solar headroom
                    // exceeds capacity), fall back to global bounds rather than
                    // making the model infeasible.
                    if (minSoc > maxSoc)
                    {
                        minSoc = spec.MinSocKWh;
                        maxSoc = spec.MaxSocKWh;
                    }
                }

                soc[t + 1] = solver.MakeNumVar(minSoc, maxSoc, $"soc_{t + 1}");

                chargeKw[t] = solver.MakeNumVar(0.0, spec.MaxChargeKW, $"charge_{t}");
                dischargeKw[t] = solver.MakeNumVar(0.0, spec.MaxDischargeKW, $"discharge_{t}");

                isCharge[t] = solver.MakeIntVar(0.0, 1.0, $"isCharge_{t}");
                isDischarge[t] = solver.MakeIntVar(0.0, 1.0, $"isDischarge_{t}");

                // Enforce charge/discharge limits via binary activity flags
                solver.Add(chargeKw[t] <= spec.MaxChargeKW * isCharge[t]);
                solver.Add(dischargeKw[t] <= spec.MaxDischargeKW * isDischarge[t]);

                // No simultaneous charge + discharge
                solver.Add(isCharge[t] + isDischarge[t] <= 1.0);
            }

            // Fix initial SOC to measured value
            solver.Add(soc[0] == Clamp(spec.InitialSocKWh, spec.MinSocKWh, spec.MaxSocKWh));

            // ----------------------------------------------------------------
            // SOC transition constraints
            // ----------------------------------------------------------------

            for (int t = 0; t < n; t++)
            {
                double netLoadKWh = pricePoints[t].NetLoadWh / 1000.0;

                // SOC dynamics:
                //   soc[t+1] = soc[t]
                //              + charge_t  * dt * η_charge
                //              - discharge_t * dt / η_discharge
                //              - net_household_load_t
                //
                // Net load is subtracted unconditionally: the MILP models the
                // battery's net energy balance. Positive net load (consumption >
                // solar) drains the battery; negative net load (solar surplus)
                // fills it. The mode-aware household-load handling in
                // BatteriesService's SOC simulation mirrors this model.
                solver.Add(
                    soc[t + 1] ==
                    soc[t]
                    + (chargeKw[t] * dtHours * spec.ChargeEfficiency)
                    - (dischargeKw[t] * dtHours / spec.DischargeEfficiency)
                    - netLoadKWh
                );
            }

            // ----------------------------------------------------------------
            // Objective: maximise profit = revenue from discharge - cost of charge
            // ----------------------------------------------------------------

            Objective objective = solver.Objective();

            for (int t = 0; t < n; t++)
            {
                double buy = pricePoints[t].BuyEurPerKWh;
                double sell = pricePoints[t].SellEurPerKWh;

                // Charging costs money (negative contribution).
                // Cycle cost is added to the effective buy price so the solver
                // only charges when the future sell price exceeds buy + cycleCost.
                objective.SetCoefficient(chargeKw[t], -(buy + opt.CycleCostEurPerKWh) * dtHours);

                // Discharging earns money (positive contribution).
                // Cycle cost is already accounted for on the charge side.
                objective.SetCoefficient(dischargeKw[t], sell * dtHours);

                // Small penalty per active quarter to prefer fewer, larger actions
                // over many tiny ones (improves battery longevity).
                if (opt.ActiveQuarterPenaltyEur != 0.0)
                {
                    objective.SetCoefficient(isCharge[t], -opt.ActiveQuarterPenaltyEur);
                    objective.SetCoefficient(isDischarge[t], -opt.ActiveQuarterPenaltyEur);
                }
            }

            objective.SetMaximization();

            // ----------------------------------------------------------------
            // Solve
            // ----------------------------------------------------------------

            var status = solver.Solve();

            bool hasSolution =
                status == Solver.ResultStatus.OPTIMAL ||
                status == Solver.ResultStatus.FEASIBLE;

            if (!hasSolution)
            {
                // Do NOT read objective.Value() or SolutionValue() here —
                // OR-Tools behaviour is undefined when no solution exists.
                return EmptyResult();
            }

            double objectiveValue = objective.Value();

            var plan = new List<PlanStep>(n);

            for (int t = 0; t < n; t++)
            {
                double cKw = chargeKw[t].SolutionValue();
                double dKw = dischargeKw[t].SolutionValue();
                double socStart = soc[t].SolutionValue();
                double socEnd = soc[t + 1].SolutionValue();

                ActionMode mode = ActionMode.Idle;

                if (cKw > 0.001 && dKw <= 0.001)
                    mode = ActionMode.Charge;
                else if (dKw > 0.001 && cKw <= 0.001)
                    mode = ActionMode.Discharge;

                plan.Add(new PlanStep(
                    Start: pricePoints[t].Start,
                    Mode: mode,
                    ChargeKW: cKw,
                    DischargeKW: dKw,
                    SocStartKWh: socStart,
                    SocEndKWh: socEnd
                ));
            }

            return new PlanResult(
                Optimal: status == Solver.ResultStatus.OPTIMAL,
                ObjectiveEur: objectiveValue,
                Plan: plan
            );
        }

        private static PlanResult EmptyResult() =>
            new PlanResult(
                Optimal: false,
                ObjectiveEur: 0.0,
                Plan: new List<PlanStep>()
            );

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}