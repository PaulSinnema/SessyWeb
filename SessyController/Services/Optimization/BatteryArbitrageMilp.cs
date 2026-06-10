using Google.OrTools.LinearSolver;

namespace SessyController.Services.Optimization
{
    public enum ActionMode { Idle = 0, Charge = 1, Discharge = 2, ZeroNetHome = 3 }

    public sealed record BatterySpec(
        double CapacityKWh,
        double InitialSocKWh,
        double MaxChargeKW,
        double MaxDischargeKW,
        double ChargeEfficiency,
        double DischargeEfficiency
    );

    public sealed record SessyOptions(
        int QuarterMinutes,
        double CycleCostEurPerKWh,
        int TimeLimitMs
    );

    public sealed record SocBound(DateTime Time, double MinSocKWh, double MaxSocKWh);

    public sealed record PlanStep(
        DateTime Start,
        ActionMode Mode,
        double ChargeKW,
        double DischargeKW,
        double SocStartKWh,
        double SocEndKWh
    );

    public sealed record PlanResult(bool Optimal, double ObjectiveEur, IReadOnlyList<PlanStep> Plan);

    /// <summary>
    /// Input per quarter for the MILP solver.
    /// NetLoadWh: household load minus solar (Wh). Positive = needs power; negative = solar surplus.
    /// </summary>
    public sealed record PricePoint(
        DateTime Start,
        double BuyEurPerKWh,
        double SellEurPerKWh,
        double NetLoadWh,
        double SolarSurplusWh
    );

    public static class BatteryArbitrageMilp
    {
        /// <summary>
        /// Profit maximization MILP with explicit ZeroNetHome and Discharging modes.
        ///
        /// Three mutually exclusive battery actions per quarter:
        ///   Charge     — grid charging (cost = buy + cc)
        ///   ZeroNetHome — discharge for own use (value = buy - cc, bounded by netLoad)
        ///   Discharging — export to grid      (value = sell - cc, only when sell > cc)
        ///
        /// This correctly separates the two discharge modes:
        ///   ZeroNetHome avoids import at buy price → profitable whenever buy > cc
        ///   Discharging exports at sell price → profitable only when sell > cc
        ///
        /// SOC transition:
        ///   soc[t+1] = soc[t] + gridCharge*dt*η - discharge*dt/η - netLoad/1000
        ///   (netLoad < 0 = solar surplus raises SOC automatically)
        /// </summary>
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

            var gridCharge = new Variable[n]; // active grid charging (kW)
            var ownUse = new Variable[n]; // ZeroNetHome: discharge for own consumption (kW)
            var export = new Variable[n]; // Discharging: export to grid (kW)
            var isCharge = new Variable[n];
            var isOwnUse = new Variable[n];
            var isExport = new Variable[n];
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
                solver.Add(gridCharge[t] <= spec.MaxChargeKW * isCharge[t]);

                // ZeroNetHome: bounded by positive net load (can't cover more than house needs).
                double maxOwnUseKw = Math.Max(pricePoints[t].NetLoadWh, 0.0) / 1000.0 / dtHours;
                ownUse[t] = solver.MakeNumVar(0.0, maxOwnUseKw, $"own_{t}");
                isOwnUse[t] = solver.MakeIntVar(0.0, 1.0, $"io_{t}");
                solver.Add(ownUse[t] <= maxOwnUseKw * isOwnUse[t]);

                // Export: full discharge capacity available.
                export[t] = solver.MakeNumVar(0.0, spec.MaxDischargeKW, $"exp_{t}");
                isExport[t] = solver.MakeIntVar(0.0, 1.0, $"ie_{t}");
                solver.Add(export[t] <= spec.MaxDischargeKW * isExport[t]);

                // Mutually exclusive: charge, ownUse, export.
                solver.Add(isCharge[t] + isOwnUse[t] + isExport[t] <= 1.0);
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
                    - (ownUse[t] + export[t]) * dtHours / spec.DischargeEfficiency
                    - netLoadKWh
                );
            }

            // ── Objective ────────────────────────────────────────────────────

            var objective = solver.Objective();

            for (int t = 0; t < n; t++)
            {
                double buy = pricePoints[t].BuyEurPerKWh;
                double sell = pricePoints[t].SellEurPerKWh;
                double cc = opt.CycleCostEurPerKWh;

                // ZeroNetHome: avoids import at buy price.
                objective.SetCoefficient(ownUse[t], (buy - cc) * dtHours);

                // Discharging: exports at sell price — only profitable when sell > cc.
                objective.SetCoefficient(export[t], (sell - cc) * dtHours);

                // Grid charging costs buy + cc.
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
                double ownKw = ownUse[t].SolutionValue();
                double expKw = export[t].SolutionValue();
                double s0 = soc[t].SolutionValue();
                double s1 = soc[t + 1].SolutionValue();

                ActionMode mode;
                double dischargeKw;

                if (cKw > 0.01)
                {
                    mode = ActionMode.Charge;
                    dischargeKw = 0.0;
                }
                else if (expKw > 0.01)
                {
                    mode = ActionMode.Discharge;
                    dischargeKw = expKw;
                }
                else if (ownKw > 0.01)
                {
                    mode = ActionMode.ZeroNetHome;
                    dischargeKw = ownKw;
                }
                else
                {
                    mode = ActionMode.Idle;
                    dischargeKw = 0.0;
                }

                plan.Add(new PlanStep(pricePoints[t].Start, mode, cKw, dischargeKw, s0, s1));
            }

            return new PlanResult(status == Solver.ResultStatus.OPTIMAL, objective.Value(), plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}