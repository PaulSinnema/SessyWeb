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
        int TimeLimitMs,
        double BeginSocCostEurPerKWh = 0.0,
        double DischargeTimePreferenceFactor = 0.0
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
    /// NetLoadWh: household load minus solar (Wh). Positive = needs grid power; negative = solar surplus.
    /// </summary>
    public sealed record PricePoint(
        DateTime Start,
        double BuyEurPerKWh,
        double SellEurPerKWh,
        double NetLoadWh,
        double SolarSurplusWh
    );

    /// <summary>
    /// Grid-balance MILP.
    ///
    /// The only decision per quarter is the battery power: charge (≥0) or discharge (≥0),
    /// mutually exclusive. Everything else follows from the grid balance:
    ///
    ///   net = netLoad + charge − discharge          (kWh over the quarter)
    ///   net = gridImport − gridExport               (import ≥ 0, export ≥ 0)
    ///
    /// where netLoad = household consumption − solar production.
    ///
    /// Cost of a quarter:
    ///   gridImport * buyPrice − gridExport * sellPrice + cycleCost * discharge
    ///
    /// Key points that match the physical reality of the Sessy:
    ///   • Charging also covers the house: extra grid import = charge + netLoad.
    ///   • Discharging also feeds the house first; only the surplus is exported.
    ///   • Self-consumed discharge is implicitly valued at the buy price (it reduces
    ///     gridImport), exported discharge at the sell price (it raises gridExport).
    ///   • Cycle cost is charged once per kWh of throughput — on discharge only — so a
    ///     full charge+discharge cycle is not double-counted.
    ///
    /// SOC transition (battery-side energy):
    ///   soc[t+1] = soc[t] + charge*η − discharge/η
    /// </summary>
    public static class BatteryArbitrageMilp
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

            // Big-M for import/export and charge/discharge exclusivity (kW).
            double bigM = Math.Max(spec.MaxChargeKW, spec.MaxDischargeKW)
                          + pricePoints.Max(p => Math.Abs(p.NetLoadWh)) / 1000.0 / dtHours
                          + 1.0;

            // ── Variables ────────────────────────────────────────────────────
            var charge = new Variable[n]; // battery charge power (kW)
            var discharge = new Variable[n]; // battery discharge power (kW)
            var isCharge = new Variable[n]; // 1 = charging this quarter
            var gridImport = new Variable[n]; // grid import (kW), ≥ 0
            var gridExport = new Variable[n]; // grid export (kW), ≥ 0
            var isImport = new Variable[n]; // 1 = importing this quarter
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

                // Charge and discharge are mutually exclusive.
                solver.Add(charge[t] <= spec.MaxChargeKW * isCharge[t]);
                solver.Add(discharge[t] <= spec.MaxDischargeKW * (1.0 - isCharge[t]));

                gridImport[t] = solver.MakeNumVar(0.0, bigM, $"imp_{t}");
                gridExport[t] = solver.MakeNumVar(0.0, bigM, $"exp_{t}");
                isImport[t] = solver.MakeIntVar(0.0, 1.0, $"ii_{t}");

                // Import and export are mutually exclusive (never pay and earn at once).
                solver.Add(gridImport[t] <= bigM * isImport[t]);
                solver.Add(gridExport[t] <= bigM * (1.0 - isImport[t]));

                // Grid balance: netLoad + charge − discharge = import − export.
                double netLoadKw = pricePoints[t].NetLoadWh / 1000.0 / dtHours;
                solver.Add(gridImport[t] - gridExport[t]
                           == netLoadKw + charge[t] - discharge[t]);
            }

            solver.Add(soc[0] == Clamp(spec.InitialSocKWh, 0.0, spec.CapacityKWh));

            // ── SOC transition (battery-side, with efficiency) ───────────────
            for (int t = 0; t < n; t++)
            {
                solver.Add(
                    soc[t + 1] ==
                    soc[t]
                    + charge[t] * dtHours * spec.ChargeEfficiency
                    - discharge[t] * dtHours / spec.DischargeEfficiency
                );
            }

            // ── Objective: minimise total grid cost ──────────────────────────
            // Cost = import*buy − export*sell + cycleCost*discharge.
            // We maximise the negative (so the solver's maximise call works uniformly).
            var objective = solver.Objective();

            for (int t = 0; t < n; t++)
            {
                double buy = pricePoints[t].BuyEurPerKWh;
                double sell = pricePoints[t].SellEurPerKWh;
                double cc = opt.CycleCostEurPerKWh;

                // Mild time preference: a discount on export revenue and discharge so the
                // solver prefers acting sooner when outcomes are otherwise equal.
                double discount = 1.0 / (1.0 + opt.DischargeTimePreferenceFactor * t);

                objective.SetCoefficient(gridImport[t], -buy * dtHours);
                objective.SetCoefficient(gridExport[t], sell * dtHours * discount);
                objective.SetCoefficient(discharge[t], -cc * dtHours);
            }

            // End-SOC water value: leftover energy is worth its acquisition cost, so the
            // solver neither hoards nor dumps it artificially at the horizon edge.
            objective.SetCoefficient(soc[n], opt.BeginSocCostEurPerKWh);

            objective.SetMaximization();

            // ── Solve ────────────────────────────────────────────────────────
            var status = solver.Solve();
            bool ok = status == Solver.ResultStatus.OPTIMAL || status == Solver.ResultStatus.FEASIBLE;
            if (!ok) return null;

            var plan = new List<PlanStep>(n);
            for (int t = 0; t < n; t++)
            {
                double cKw = charge[t].SolutionValue();
                double dKw = discharge[t].SolutionValue();
                double expKw = gridExport[t].SolutionValue();
                double s0 = soc[t].SolutionValue();
                double s1 = soc[t + 1].SolutionValue();

                ActionMode mode;
                if (cKw > 0.01)
                {
                    mode = ActionMode.Charge;
                }
                else if (dKw > 0.01)
                {
                    // Discharging that exports to grid = Discharge; discharge that only
                    // covers the house (no export) = ZeroNetHome.
                    mode = expKw > 0.01 ? ActionMode.Discharge : ActionMode.ZeroNetHome;
                }
                else
                {
                    mode = ActionMode.Idle;
                }

                plan.Add(new PlanStep(pricePoints[t].Start, mode, cKw, dKw, s0, s1));
            }

            return new PlanResult(status == Solver.ResultStatus.OPTIMAL, objective.Value(), plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}