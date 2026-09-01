using SessyController.Services.Items;

namespace SessyController.Services.Optimization
{
    /// <summary>
    /// Dynamic-programming battery planner over (quarter, SOC-level). Shadow only — runs alongside
    /// BatteryGreedyPlanner to compare objectives; it does not drive the batteries yet.
    ///
    /// It shares the greedy planner's constraints and objective definition so the comparison is
    /// fair: the same power-dependent efficiency curve, charge taper, charge floor, discharge
    /// capability, per-quarter SOC bounds, ReserveOnly rules, cycle cost and carry-forward terminal
    /// value. It maximizes the SAME objective the greedy reports (undiscounted prices + carry-forward),
    /// so a positive objective delta is real headroom, not a modelling artefact.
    ///
    /// Actions per quarter: idle, or one charge/discharge of an AC-side energy block up to the
    /// quarter's tapered/capped limit. SOC is discretised into Levels states; a transition snaps the
    /// resulting SOC to the nearest level. That snapping is why this is a shadow measurement, not a
    /// setpoint source.
    /// </summary>
    public static class BatteryDpPlanner
    {
        private const int Levels = 81;        // SOC grid resolution
        private const double BlockKWh = 0.20; // AC action granularity
        private const double Eps = 1e-6;
        private const double NegInf = -1e18;

        public static PlanResult? Solve(
            IReadOnlyList<PricePoint> pricePoints,
            BatterySpec spec,
            SessyOptions opt,
            IReadOnlyList<SocBound> socBounds)
        {
            if (pricePoints == null || pricePoints.Count == 0) return null;

            int n = pricePoints.Count;
            double dt = opt.QuarterMinutes / 60.0;
            double chEff0 = Clamp(spec.ChargeEfficiency, 0.05, 1.0);
            double disEff0 = Clamp(spec.DischargeEfficiency, 0.05, 1.0);
            double cycleCost = Math.Max(0.0, opt.CycleCostEurPerKWh);
            double capacity = Math.Max(0.0, spec.CapacityKWh);
            if (capacity <= Eps) return new PlanResult(true, 0.0, EmptyPlan(pricePoints));

            var efficiency = spec.Efficiency ?? EfficiencyCurve.Flat(chEff0, disEff0);
            var taper = spec.ChargeTaper ?? ChargeTaper.None;
            var chargeFloor = spec.ChargeFloor ?? ChargeCapabilityFloor.None;
            var dischargeCapability = spec.DischargeCapability ?? DischargeCapability.None;

            double chEffFor(double ac) => efficiency.ChargeAt(Math.Max(0.0, ac) / dt);
            double disEffFor(double ac) => efficiency.DischargeAt(Math.Max(0.0, ac) / dt);
            double disEffFull = efficiency.DischargeAt(Math.Max(0.1, spec.MaxDischargeKW));
            double chEffFull = efficiency.ChargeAt(Math.Max(0.1, spec.MaxChargeKW));

            var minSoc = new double[n];
            var maxSoc = new double[n];
            var maxChargeKWh = new double[n];
            var maxDischargeKWh = new double[n];
            for (int t = 0; t < n; t++)
            {
                double cKw = pricePoints[t].MaxChargeKW ?? spec.MaxChargeKW;
                double dKw = pricePoints[t].MaxDischargeKW ?? spec.MaxDischargeKW;
                maxChargeKWh[t] = Math.Max(0.0, cKw) * dt;
                maxDischargeKWh[t] = Math.Max(0.0, dKw) * dt;
                double mn = 0.0, mx = capacity;
                if (socBounds != null && t < socBounds.Count)
                {
                    mn = Clamp(socBounds[t].MinSocKWh, 0.0, capacity);
                    mx = Clamp(socBounds[t].MaxSocKWh, mn, capacity);
                }
                minSoc[t] = mn; maxSoc[t] = mx;
            }

            double taperedChargeKWh(int t, double soc)
            {
                double cap = maxChargeKWh[t];
                double frac = soc / capacity;
                double tapered = cap;
                if (taper.Samples > 0)
                {
                    double temp = pricePoints[t].TemperatureC ?? ChargeTaper.RefTemperatureC;
                    double mean48h = pricePoints[t].Temperature48hC ?? temp;
                    tapered = Math.Min(cap, Math.Max(0.0, spec.MaxChargeKW) * taper.Ratio(frac, temp, mean48h) * dt);
                }
                double floorKWh = chargeFloor.PowerW(frac) / 1000.0 * dt;
                return Math.Min(cap, Math.Max(tapered, floorKWh));
            }
            double cappedDischargeKWh(int t, double soc)
            {
                double cap = maxDischargeKWh[t];
                if (dischargeCapability.Samples == 0) return cap;
                return Math.Min(cap, dischargeCapability.PowerW(soc / capacity) / 1000.0 * dt);
            }

            double socOf(int idx) => idx / (double)(Levels - 1) * capacity;
            int idxOf(double soc) => (int)Math.Round(Clamp(soc, 0.0, capacity) / capacity * (Levels - 1));

            // Reward and resulting SOC for an action at (t, soc). chargeAc>0 charges, disAc>0 discharges.
            (double reward, double socNext, bool ok) Step(int t, double soc, double chargeAc, double disAc)
            {
                var p = pricePoints[t];
                double netLoad = p.NetLoadWh / 1000.0;
                double surplus = netLoad < 0 ? -netLoad : 0.0;
                double deficit = netLoad > 0 ? netLoad : 0.0;
                double import, export, socNext;

                if (chargeAc > Eps)
                {
                    if (chargeAc > taperedChargeKWh(t, soc) + Eps) return (0, 0, false);
                    socNext = soc + chargeAc * chEffFor(chargeAc);
                    if (socNext > maxSoc[t] + Eps) return (0, 0, false);
                    double fromSolar = Math.Min(chargeAc, surplus);
                    double gridCharge = chargeAc - fromSolar;
                    if (p.ReserveOnly && gridCharge > Eps) return (0, 0, false); // no grid charge on predicted quarters
                    export = surplus - fromSolar;
                    import = deficit + gridCharge;
                }
                else if (disAc > Eps)
                {
                    if (disAc > cappedDischargeKWh(t, soc) + Eps) return (0, 0, false);
                    socNext = soc - disAc / disEffFor(disAc);
                    if (socNext < minSoc[t] - Eps) return (0, 0, false);
                    double toHouse = Math.Min(disAc, deficit);
                    double batteryExport = disAc - toHouse;
                    if (batteryExport > Eps && (!opt.AllowExport || p.ReserveOnly)) return (0, 0, false);
                    import = deficit - toHouse;
                    export = surplus + batteryExport;
                }
                else
                {
                    socNext = soc; import = deficit; export = surplus;
                }

                double reward = export * p.SellEurPerKWh - import * p.BuyEurPerKWh - disAc * cycleCost;
                return (reward, Clamp(socNext, 0.0, capacity), true);
            }

            // Candidate AC action magnitudes (0, then blocks up to the per-quarter cap).
            double capMax = Math.Max(maxChargeKWh.Length > 0 ? Max(maxChargeKWh) : 0, Max(maxDischargeKWh));
            int steps = Math.Max(1, (int)Math.Ceiling(capMax / BlockKWh));

            // ── Backward DP ──────────────────────────────────────────────────
            var V = new double[n + 1][];
            var polCharge = new double[n][];
            var polDis = new double[n][];
            V[n] = new double[Levels];
            bool carry = opt.AllowCarryForward && opt.ReplacementCostEurPerKWh > 0.0;
            for (int s = 0; s < Levels; s++)
                V[n][s] = carry ? socOf(s) / chEffFull * opt.ReplacementCostEurPerKWh : 0.0;

            for (int t = n - 1; t >= 0; t--)
            {
                V[t] = new double[Levels];
                polCharge[t] = new double[Levels];
                polDis[t] = new double[Levels];
                for (int s = 0; s < Levels; s++)
                {
                    double soc = socOf(s);
                    double best = NegInf, bc = 0, bd = 0;
                    // idle
                    var e0 = Step(t, soc, 0, 0);
                    if (e0.ok) { double v = e0.reward + V[t + 1][idxOf(e0.socNext)]; if (v > best) { best = v; bc = 0; bd = 0; } }
                    // charge blocks
                    for (int k = 1; k <= steps; k++)
                    {
                        double c = Math.Min(k * BlockKWh, taperedChargeKWh(t, soc));
                        if (c <= Eps) break;
                        var e = Step(t, soc, c, 0);
                        if (e.ok) { double v = e.reward + V[t + 1][idxOf(e.socNext)]; if (v > best) { best = v; bc = c; bd = 0; } }
                        if (c >= taperedChargeKWh(t, soc) - Eps) break;
                    }
                    // discharge blocks
                    for (int k = 1; k <= steps; k++)
                    {
                        double d = Math.Min(k * BlockKWh, cappedDischargeKWh(t, soc));
                        if (d <= Eps) break;
                        var e = Step(t, soc, 0, d);
                        if (e.ok) { double v = e.reward + V[t + 1][idxOf(e.socNext)]; if (v > best) { best = v; bc = 0; bd = d; } }
                        if (d >= cappedDischargeKWh(t, soc) - Eps) break;
                    }
                    V[t][s] = best <= NegInf / 2 ? V[t + 1][s] : best;
                    polCharge[t][s] = bc; polDis[t][s] = bd;
                }
            }

            // ── Forward extract ──────────────────────────────────────────────
            var plan = new List<PlanStep>(n);
            double objective = 0.0;
            double socCur = Clamp(spec.InitialSocKWh, 0.0, capacity);
            for (int t = 0; t < n; t++)
            {
                int s = idxOf(socCur);
                double c = polCharge[t][s], d = polDis[t][s];
                var e = Step(t, socCur, c, d);
                double socStart = socCur;
                double socEnd = e.ok ? e.socNext : socCur;
                objective += e.ok ? e.reward : 0.0;

                double netLoad = pricePoints[t].NetLoadWh / 1000.0;
                double deficit = netLoad > 0 ? netLoad : 0.0;
                double gridCharge = c > Eps ? Math.Max(0.0, c - (netLoad < 0 ? -netLoad : 0.0)) : 0.0;
                double batteryExport = d > Eps ? Math.Max(0.0, d - deficit) : 0.0;
                ActionMode mode = gridCharge > Eps ? ActionMode.Charge
                                : batteryExport > Eps ? ActionMode.Discharge
                                : ActionMode.ZeroNetHome;

                plan.Add(new PlanStep(pricePoints[t].Start, mode,
                    ChargeKW: c / dt, DischargeKW: d / dt,
                    SocStartKWh: socStart, SocEndKWh: socEnd,
                    RequestedChargeKW: c / dt, RequestedDischargeKW: d / dt));
                socCur = socEnd;
            }
            if (carry)
                objective += socCur / chEffFull * opt.ReplacementCostEurPerKWh;

            return new PlanResult(true, objective, plan);
        }

        private static List<PlanStep> EmptyPlan(IReadOnlyList<PricePoint> pts)
        {
            var l = new List<PlanStep>(pts.Count);
            foreach (var p in pts)
                l.Add(new PlanStep(p.Start, ActionMode.ZeroNetHome, 0, 0, 0, 0, 0, 0));
            return l;
        }

        private static double Max(double[] a) { double m = 0; foreach (var v in a) if (v > m) m = v; return m; }
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
