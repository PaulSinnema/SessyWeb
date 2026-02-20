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
        int TimeLimitMs
    );

    public sealed record PlanStep(
        DateTime Start,
        ActionMode Mode,
        double ChargeKW,
        double DischargeKW,
        double SocKWh
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
        public static PlanResult Solve(
            IReadOnlyList<PricePoint> points,
            BatterySpec spec,
            SessyOptions opt)
        {
            if (points == null || points.Count == 0)
                return new PlanResult(false, 0.0, Array.Empty<PlanStep>());

            // Create MILP solver
            var solver = Solver.CreateSolver("CBC_MIXED_INTEGER_PROGRAMMING");
            if (solver == null)
                return new PlanResult(false, 0.0, Array.Empty<PlanStep>());

            solver.SetTimeLimit(opt.TimeLimitMs);

            int n = points.Count;
            double dtHours = opt.QuarterMinutes / 60.0;

            // Decision vars: binary charge/discharge
            var xCh = new Variable[n];
            var xDis = new Variable[n];

            for (int t = 0; t < n; t++)
            {
                xCh[t] = solver.MakeBoolVar($"xCh_{t}");
                xDis[t] = solver.MakeBoolVar($"xDis_{t}");

                if (opt.ForbidSimultaneousChargeDischarge)
                {
                    // xCh + xDis <= 1
                    solver.Add(xCh[t] + xDis[t] <= 1.0);
                }
            }

            // SOC vars (n+1)
            var soc = new Variable[n + 1];
            for (int t = 0; t <= n; t++)
                soc[t] = solver.MakeNumVar(spec.MinSocKWh, spec.MaxSocKWh, $"soc_{t}");

            // Initial SOC fixed
            solver.Add(soc[0] == Clamp(spec.InitialSocKWh, spec.MinSocKWh, spec.MaxSocKWh));

            // SOC dynamics
            // soc[t+1] = soc[t] + (chargeKW*dt*etaCh) - (disKW*dt/etaDis) - netLoadKWh
            // netLoadKWh = points[t].NetLoadWh / 1000
            for (int t = 0; t < n; t++)
            {
                double netLoadKWh = points[t].NetLoadWh / 1000.0;

                // Fixed max-power energy per quarter
                double chEnergyKWh = spec.MaxChargeKW * dtHours * spec.ChargeEfficiency;
                double disEnergyKWh = spec.MaxDischargeKW * dtHours / Math.Max(1e-9, spec.DischargeEfficiency);

                solver.Add(
                    soc[t + 1] ==
                    soc[t]
                    + xCh[t] * chEnergyKWh
                    - xDis[t] * disEnergyKWh
                    - netLoadKWh
                );
            }

            // Objective: maximize profit
            // Revenue from discharging: sellPrice * dischargedEnergy
            // Cost for charging: buyPrice * chargedEnergy
            // Optional penalty per active quarter to reduce flicker.
            var obj = solver.Objective();
            obj.SetMaximization();

            for (int t = 0; t < n; t++)
            {
                double buy = points[t].BuyEurPerKWh;
                double sell = points[t].SellEurPerKWh;

                // Energy traded with grid (kWh per quarter)
                double chGridKWh = spec.MaxChargeKW * dtHours;    // what you buy from grid (before eff)
                double disGridKWh = spec.MaxDischargeKW * dtHours; // what you sell to grid

                // Profit contribution
                // NOTE: You could model eff on grid side differently; for now:
                // - grid buy is chGridKWh
                // - grid sell is disGridKWh
                obj.SetCoefficient(xCh[t], obj.GetCoefficient(xCh[t]) - buy * chGridKWh - opt.ActiveQuarterPenaltyEur);
                obj.SetCoefficient(xDis[t], obj.GetCoefficient(xDis[t]) + sell * disGridKWh - opt.ActiveQuarterPenaltyEur);
            }

            var status = solver.Solve();

            bool optimal =
                status == Solver.ResultStatus.OPTIMAL ||
                status == Solver.ResultStatus.FEASIBLE;

            double objective = optimal ? obj.Value() : 0.0;

            var plan = new List<PlanStep>(n);

            // Build plan output (use SOC at end of quarter t -> soc[t+1])
            for (int t = 0; t < n; t++)
            {
                var mode = ActionMode.Idle;

                double ch = xCh[t].SolutionValue() > 0.5 ? spec.MaxChargeKW : 0.0;
                double dis = xDis[t].SolutionValue() > 0.5 ? spec.MaxDischargeKW : 0.0;

                if (ch > 0.0) mode = ActionMode.Charge;
                else if (dis > 0.0) mode = ActionMode.Discharge;

                plan.Add(new PlanStep(
                    Start: points[t].Start,
                    Mode: mode,
                    ChargeKW: ch,
                    DischargeKW: dis,
                    SocKWh: soc[t + 1].SolutionValue()
                ));
            }

            return new PlanResult(optimal, objective, plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}
