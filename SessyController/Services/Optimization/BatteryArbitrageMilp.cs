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
        public static PlanResult Solve(
    IReadOnlyList<PricePoint> pricePoints,
    BatterySpec spec,
    SessyOptions opt)
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

            var chargeKw = new Variable[n];
            var dischargeKw = new Variable[n];
            var isCharge = new Variable[n];
            var isDischarge = new Variable[n];
            var soc = new Variable[n + 1];

            for (int t = 0; t <= n; t++)
            {
                soc[t] = solver.MakeNumVar(spec.MinSocKWh, spec.MaxSocKWh, $"soc_{t}");
            }

            solver.Add(soc[0] == Math.Max(spec.MinSocKWh, Math.Min(spec.MaxSocKWh, spec.InitialSocKWh)));

            for (int t = 0; t < n; t++)
            {
                chargeKw[t] = solver.MakeNumVar(0.0, spec.MaxChargeKW, $"charge_{t}");
                dischargeKw[t] = solver.MakeNumVar(0.0, spec.MaxDischargeKW, $"discharge_{t}");

                isCharge[t] = solver.MakeIntVar(0.0, 1.0, $"isCharge_{t}");
                isDischarge[t] = solver.MakeIntVar(0.0, 1.0, $"isDischarge_{t}");

                solver.Add(chargeKw[t] <= spec.MaxChargeKW * isCharge[t]);
                solver.Add(dischargeKw[t] <= spec.MaxDischargeKW * isDischarge[t]);
                solver.Add(isCharge[t] + isDischarge[t] <= 1.0);

                double netLoadKWh = pricePoints[t].NetLoadWh / 1000.0;

                solver.Add(
                    soc[t + 1] ==
                    soc[t]
                    + (chargeKw[t] * dtHours * spec.ChargeEfficiency)
                    - (dischargeKw[t] * dtHours / spec.DischargeEfficiency)
                    - netLoadKWh
                );
            }

            Objective objective = solver.Objective();

            for (int t = 0; t < n; t++)
            {
                double buy = pricePoints[t].BuyEurPerKWh;
                double sell = pricePoints[t].SellEurPerKWh;

                objective.SetCoefficient(chargeKw[t], -buy * dtHours);
                objective.SetCoefficient(dischargeKw[t], sell * dtHours);

                if (opt.ActiveQuarterPenaltyEur != 0.0)
                {
                    objective.SetCoefficient(isCharge[t], -opt.ActiveQuarterPenaltyEur);
                    objective.SetCoefficient(isDischarge[t], -opt.ActiveQuarterPenaltyEur);
                }
            }

            objective.SetMaximization();

            var status = solver.Solve();

            bool hasSolution =
                status == Solver.ResultStatus.OPTIMAL ||
                status == Solver.ResultStatus.FEASIBLE;

            if (!hasSolution)
            {
                // VERY IMPORTANT:
                // Do not read objective.Value() or any SolutionValue() here.
                return EmptyResult();
            }

            // Only now it is safe to read solution values.
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

        private static PlanResult EmptyResult()
        {
            return new PlanResult(
                Optimal: false,
                ObjectiveEur: 0.0,
                Plan: new List<PlanStep>()
            );
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}
