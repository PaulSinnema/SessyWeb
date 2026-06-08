using Google.OrTools.LinearSolver;

namespace SessyController.Services.Optimization
{
    public enum ActionMode { Idle = 0, Charge = 1, Discharge = 2 }

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

    /// <summary>
    /// Per-quarter SOC bounds.
    /// MinSocKWh: minimum SOC at end of this quarter (night reserve).
    /// MaxSocKWh: maximum SOC at end of this quarter (solar headroom).
    /// </summary>
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
    /// BuyEurPerKWh:   full consumer buy price incl. taxes.
    /// SellEurPerKWh:  sell/export price.
    /// NetLoadWh:      household load minus solar (Wh). Positive = needs power; negative = solar surplus.
    /// SolarSurplusWh: unused — kept for future logging.
    /// </summary>
    public sealed record PricePoint(
        DateTime Start,
        double BuyEurPerKWh,
        double SellEurPerKWh,
        double NetLoadWh,
        double SolarSurplusWh,
        double EffectiveChargeCostEurPerKWh = -1.0  // -1 = use BuyEurPerKWh
    );

    public static class BatteryArbitrageMilp
    {
        /// <summary>
        /// Solve the battery arbitrage MILP for profit maximisation.
        ///
        /// Objective (maximise):
        ///   discharge_t * max(buy_t, sell_t)   — discharging avoids import or earns export revenue
        ///   - gridCharge_t * buy_t              — grid charging costs money
        ///   - cycleCost * (gridCharge_t + discharge_t)
        ///
        /// ZeroNetHome (the inverter's self-regulation mode) handles household load coverage
        /// automatically — the solver does NOT need to model per-quarter own-use.
        /// The solver's job is purely to decide WHEN to charge/discharge for maximum arbitrage.
        ///
        /// SOC transition:
        ///   soc[t+1] = soc[t] + gridCharge[t]*dt*η_c - discharge[t]*dt/η_d - netLoadKWh[t]
        ///
        /// netLoadKWh < 0 (solar surplus) raises SOC automatically.
        /// netLoadKWh > 0 (household load) drains SOC automatically.
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

            var gridCharge = new Variable[n];
            var discharge = new Variable[n];
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
                discharge[t] = solver.MakeNumVar(0.0, spec.MaxDischargeKW, $"dis_{t}");
                isCharge[t] = solver.MakeIntVar(0.0, 1.0, $"ic_{t}");
                isDischarge[t] = solver.MakeIntVar(0.0, 1.0, $"id_{t}");

                solver.Add(gridCharge[t] <= spec.MaxChargeKW * isCharge[t]);
                solver.Add(discharge[t] <= spec.MaxDischargeKW * isDischarge[t]);
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
            // discharge earns max(buy, sell): if sell > buy (e.g. negative prices) export wins;
            // otherwise avoiding import at buy price is the value.
            // grid charging costs buy + cycleCost.

            var objective = solver.Objective();

            for (int t = 0; t < n; t++)
            {
                double buy = pricePoints[t].BuyEurPerKWh;
                double sell = pricePoints[t].SellEurPerKWh;
                double cc = opt.CycleCostEurPerKWh;
                double chargeCost = pricePoints[t].EffectiveChargeCostEurPerKWh >= 0.0
                    ? pricePoints[t].EffectiveChargeCostEurPerKWh
                    : buy;

                // Discharge value = max(buy, sell) minus threshold and cycle cost.
                // Add a small penalty when discharge is not profitable to prevent the solver
                // from discharging arbitrarily when indifferent (dischargeValue = 0).
                double dischargeValue = Math.Max(buy, sell) - chargeCost - cc;
                double dischargeCoeff = dischargeValue > 0.001
                    ? dischargeValue * dtHours
                    : -0.001 * dtHours;  // small penalty to prefer ZeroNetHome

                objective.SetCoefficient(discharge[t], dischargeCoeff);

                // Grid charging is only worthwhile at low prices (future discharge will be profitable).
                objective.SetCoefficient(gridCharge[t], -(chargeCost + cc) * dtHours);
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