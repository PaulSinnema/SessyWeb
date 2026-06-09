using Google.OrTools.LinearSolver;

namespace SessyController.Services.Optimization
{
    /// <summary>
    /// Self-consumption MILP solver.
    ///
    /// Objective (maximise):
    ///   ownUse_t * buyPrice_t          — discharge covers household load (avoided import)
    ///   - gridCharge_t * buyPrice_t    — grid charging costs money
    ///   - cycleCost * (gridCharge_t + discharge_t)
    ///
    /// Key difference from ProfitMaximization:
    ///   - Discharge is bounded to positive net load only (ownUse = discharge, no export).
    ///   - No selling to grid — the battery only covers household consumption.
    ///   - Solar surplus is stored and used for own consumption later.
    ///
    /// SOC transition identical to BatteryArbitrageMilp.
    /// </summary>
    public static class SelfConsumptionMilp
    {
        public static PlanResult? Solve(
            IReadOnlyList<PricePoint> pricePoints,
            BatterySpec spec,
            SessyOptions opt,
            IReadOnlyList<SocBound> socBounds)
        {
            if (pricePoints == null || pricePoints.Count == 0) return null;

            var solver = Solver.CreateSolver("CBC_MIXED_INTEGER_PROGRAMMING");
            if (solver == null) return null;

            if (opt.TimeLimitMs > 0)
                solver.SetTimeLimit(opt.TimeLimitMs);

            int n = pricePoints.Count;
            double dtHours = opt.QuarterMinutes / 60.0;

            var boundByTime = socBounds.ToDictionary(b => b.Time);

            // ── Variables ────────────────────────────────────────────────────

            var gridCharge = new Variable[n];
            var discharge = new Variable[n]; // bounded to household load — no export
            var isCharge = new Variable[n];
            var isDischarge = new Variable[n];
            var soc = new Variable[n + 1];

            soc[0] = solver.MakeNumVar(0.0, spec.CapacityKWh, "soc_0");

            for (int t = 0; t < n; t++)
            {
                double mn = 0.0, mx = spec.CapacityKWh;
                if (boundByTime.TryGetValue(pricePoints[t].Start, out var b))
                {
                    mn = Math.Max(0.0, Math.Min(b.MinSocKWh, spec.CapacityKWh));
                    mx = Math.Max(mn, Math.Min(b.MaxSocKWh, spec.CapacityKWh));
                }

                soc[t + 1] = solver.MakeNumVar(mn, mx, $"soc_{t + 1}");
                gridCharge[t] = solver.MakeNumVar(0.0, spec.MaxChargeKW, $"gc_{t}");
                isCharge[t] = solver.MakeIntVar(0.0, 1.0, $"ic_{t}");
                isDischarge[t] = solver.MakeIntVar(0.0, 1.0, $"id_{t}");

                // Discharge bounded to positive net load only — no export to grid.
                double maxOwnUseKw = Math.Max(pricePoints[t].NetLoadWh, 0.0) / 1000.0 / dtHours;
                discharge[t] = solver.MakeNumVar(0.0, maxOwnUseKw, $"dis_{t}");

                solver.Add(gridCharge[t] <= spec.MaxChargeKW * isCharge[t]);
                solver.Add(discharge[t] <= maxOwnUseKw * isDischarge[t]);
                solver.Add(isCharge[t] + isDischarge[t] <= 1.0);
            }

            solver.Add(soc[0] == Clamp(spec.InitialSocKWh, 0.0, spec.CapacityKWh));

            // ── SOC transition ───────────────────────────────────────────────

            for (int t = 0; t < n; t++)
            {
                double netLoadKWh = pricePoints[t].NetLoadWh / 1000.0;

                solver.Add(
                    soc[t + 1] ==
                    soc[t]
                    + gridCharge[t] * dtHours * spec.ChargeEfficiency
                    - discharge[t] * dtHours / spec.DischargeEfficiency
                    - netLoadKWh
                );
            }

            // ── Objective ────────────────────────────────────────────────────
            // Only own-use discharge is rewarded — no export value.
            // Charging costs buy price + cycle cost.

            var objective = solver.Objective();

            for (int t = 0; t < n; t++)
            {
                double buy = pricePoints[t].BuyEurPerKWh;
                double cc = opt.CycleCostEurPerKWh;

                // Discharge = own use only → value = avoided import at buy price.
                objective.SetCoefficient(discharge[t], (buy - cc) * dtHours);
                objective.SetCoefficient(gridCharge[t], -(buy + cc) * dtHours);
            }

            objective.SetMaximization();

            // ── Solve ────────────────────────────────────────────────────────

            var status = solver.Solve();
            bool ok = status == Solver.ResultStatus.OPTIMAL || status == Solver.ResultStatus.FEASIBLE;
            if (!ok) return null;

            var plan = new List<PlanStep>(n);
            for (int t = 0; t < n; t++)
            {
                double cKw = gridCharge[t].SolutionValue();
                double dKw = discharge[t].SolutionValue();
                double s0 = soc[t].SolutionValue();
                double s1 = soc[t + 1].SolutionValue();

                ActionMode mode = ActionMode.Idle;
                if (cKw > 0.01) mode = ActionMode.Charge;
                else if (dKw > 0.01) mode = ActionMode.Discharge;

                plan.Add(new PlanStep(pricePoints[t].Start, mode, cKw, dKw, s0, s1));
            }

            return new PlanResult(status == Solver.ResultStatus.OPTIMAL, objective.Value(), plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}