using Google.OrTools.LinearSolver;

namespace SessyController.Services.Optimization
{
    /// <summary>
    /// Self-consumption MILP solver.
    ///
    /// Same grid-balance model as BatteryArbitrageMilp, with one restriction: the battery
    /// never exports to the grid. It may charge (from grid or solar surplus) and discharge
    /// only up to the household load, so discharge always stays "behind the meter".
    ///
    /// Grid balance per quarter:
    ///   gridImport − gridExport = netLoad + charge − discharge
    /// with gridExport forced to 0 (no selling). Any solar surplus that is not stored is
    /// therefore lost rather than sold — which is the point of a self-consumption profile.
    ///
    ///   netLoad = household consumption − solar production
    ///
    /// Cost of a quarter: gridImport * buyPrice + cycleCost * discharge.
    /// (Self-consumed discharge is implicitly valued at the buy price because it lowers
    ///  gridImport. Cycle cost is charged once, on discharge only.)
    ///
    /// SOC transition (battery-side, with efficiency):
    ///   soc[t+1] = soc[t] + charge*η − discharge/η
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

            double bigM = Math.Max(spec.MaxChargeKW, spec.MaxDischargeKW)
                          + pricePoints.Max(p => Math.Abs(p.NetLoadWh)) / 1000.0 / dtHours
                          + 1.0;

            var charge = new Variable[n];
            var discharge = new Variable[n];
            var isCharge = new Variable[n];
            var gridImport = new Variable[n];
            var curtail = new Variable[n]; // solar surplus that is neither stored nor used
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

                charge[t] = solver.MakeNumVar(0.0, spec.MaxChargeKW, $"chg_{t}");
                discharge[t] = solver.MakeNumVar(0.0, spec.MaxDischargeKW, $"dis_{t}");
                isCharge[t] = solver.MakeIntVar(0.0, 1.0, $"ic_{t}");

                solver.Add(charge[t] <= spec.MaxChargeKW * isCharge[t]);
                solver.Add(discharge[t] <= spec.MaxDischargeKW * (1.0 - isCharge[t]));

                gridImport[t] = solver.MakeNumVar(0.0, bigM, $"imp_{t}");
                curtail[t] = solver.MakeNumVar(0.0, bigM, $"cur_{t}");

                // Grid balance with NO paid export, but solar surplus that cannot be stored
                // is allowed to leave as unrewarded curtailment (or zero-value feed-in):
                //   import − curtail = netLoad + charge − discharge
                // Both import and curtail are ≥ 0 and earn nothing on the curtail side, so
                // the solver never curtails unless the battery is full and load is covered.
                double netLoadKw = pricePoints[t].NetLoadWh / 1000.0 / dtHours;
                solver.Add(gridImport[t] - curtail[t]
                           == netLoadKw + charge[t] - discharge[t]);
            }

            solver.Add(soc[0] == Clamp(spec.InitialSocKWh, 0.0, spec.CapacityKWh));

            for (int t = 0; t < n; t++)
            {
                solver.Add(
                    soc[t + 1] ==
                    soc[t]
                    + charge[t] * dtHours * spec.ChargeEfficiency
                    - discharge[t] * dtHours / spec.DischargeEfficiency
                );
            }

            // ── Objective: minimise grid cost (maximise its negative) ────────
            var objective = solver.Objective();

            for (int t = 0; t < n; t++)
            {
                double buy = pricePoints[t].BuyEurPerKWh;
                double cc = opt.CycleCostEurPerKWh;
                double discount = 1.0 / (1.0 + opt.DischargeTimePreferenceFactor * t);

                objective.SetCoefficient(gridImport[t], -buy * dtHours);
                objective.SetCoefficient(discharge[t], -cc * dtHours * discount);
            }

            objective.SetCoefficient(soc[n], opt.BeginSocCostEurPerKWh);
            objective.SetMaximization();

            var status = solver.Solve();
            bool ok = status == Solver.ResultStatus.OPTIMAL || status == Solver.ResultStatus.FEASIBLE;
            if (!ok) return null;

            var plan = new List<PlanStep>(n);
            for (int t = 0; t < n; t++)
            {
                double cKw = charge[t].SolutionValue();
                double dKw = discharge[t].SolutionValue();
                double s0 = soc[t].SolutionValue();
                double s1 = soc[t + 1].SolutionValue();

                // No export here, so any discharge is ZeroNetHome.
                ActionMode mode;
                if (cKw > 0.01) mode = ActionMode.Charge;
                else if (dKw > 0.01) mode = ActionMode.ZeroNetHome;
                else mode = ActionMode.Idle;

                plan.Add(new PlanStep(pricePoints[t].Start, mode, cKw, dKw, s0, s1));
            }

            return new PlanResult(status == Solver.ResultStatus.OPTIMAL, objective.Value(), plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}