namespace SessyController.Services.Optimization
{
    /// <summary>
    /// Deterministic, greedy battery planner.
    ///
    /// Why greedy instead of a MILP: for a single battery driven by a price curve the optimal
    /// policy is essentially "charge in the cheapest quarters, discharge in the most expensive
    /// ones, keep a night reserve". A MILP can express that, but it needs an end-of-horizon
    /// value, a time-preference discount and several guards to behave — knobs that fight each
    /// other and can make the model infeasible. This planner encodes the policy directly:
    /// it always returns a plan, and every decision can be traced back to one comparison.
    ///
    /// The algorithm:
    ///
    ///  1. Baseline pass (self-consumption / ZeroNetHome).
    ///     Solar surplus charges the battery, household deficit is served from the battery,
    ///     both within SOC bounds and power limits. Whatever remains is exported / imported.
    ///
    ///  2. Near-term hedge pass (optional, off by default).
    ///     Pure winner-takes-all arbitrage always chases the single highest-value quarter in
    ///     the whole horizon. If a much better peak appears far out (e.g. tomorrow evening
    ///     outbidding tonight), it can claim the entire stock and leave a nearer, still
    ///     profitable quarter untouched. When SessyOptions.NearTermHedgeHours &gt; 0, this pass
    ///     earmarks NearTermHedgeFraction of the currently available stock for the nearest
    ///     quarter within that window whose value clears the cycle cost, before arbitrage runs.
    ///     It does not pick the best quarter in the window — only the nearest profitable one —
    ///     so a genuinely better nearby opportunity is not required, just a good-enough one.
    ///
    ///  3. Arbitrage pass, in small energy blocks.
    ///     Repeatedly find the most profitable feasible (charge i → discharge j, i &lt; j) pair
    ///     and allocate one block to it, until no profitable pair remains.
    ///
    ///     Marginal value of discharging at j:
    ///        buy[j]   while it still displaces a grid import (avoided cost), else
    ///        sell[j]  because the energy is exported (only when export is allowed there).
    ///
    ///     Marginal cost of charging at i:
    ///        sell[i]  while solar surplus at i would otherwise be exported (opportunity cost), else
    ///        buy[i]   because the energy is imported from the grid.
    ///
    ///     Delivering E kWh at j drains E / dischargeEfficiency from the store, which in turn
    ///     needs E / (dischargeEfficiency * chargeEfficiency) kWh on the AC side at i. Hence
    ///
    ///        profit(E) = E * ( value[j] − cost[i] / (chargeEff * dischargeEff) − cycleCost )
    ///
    ///     A pair is feasible when the charge fits in i's remaining charge power, the discharge
    ///     fits in j's remaining discharge power, and raising the SOC across (i, j] keeps it at
    ///     or below the maximum SOC on every quarter in between.
    ///
    ///  4. Classification.
    ///     Charging fed by the grid → Charge. Discharging that exports → Discharge.
    ///     Everything else (storing solar, covering the house) → ZeroNetHome.
    ///
    /// When SessyOptions.AllowExport is false the battery never pushes energy to the grid; it
    /// only stores solar and covers the household load (self-consumption strategy).
    /// The planner is deterministic and always returns a plan — it can never be infeasible.
    /// </summary>
    public static class BatteryGreedyPlanner
    {
        /// <summary>Energy allocated per arbitrage iteration (kWh delivered at the discharge quarter).</summary>
        private const double BlockKWh = 0.10;

        /// <summary>Safety valve so a pathological input can never spin forever.</summary>
        private const int MaxIterations = 5000;

        /// <summary>Values below this are treated as zero (kW / kWh).</summary>
        private const double Eps = 1e-6;

        /// <summary>Sentinel: no profitable pair found this iteration.</summary>
        private const int NoSource = -1;

        /// <summary>Sentinel: the discharge is fed from the initial stock, not from a charge quarter.</summary>
        private const int StockSource = -2;

        public static PlanResult? Solve(
            IReadOnlyList<PricePoint> pricePoints,
            BatterySpec spec,
            SessyOptions opt,
            IReadOnlyList<SocBound> socBounds)
        {
            if (pricePoints == null || pricePoints.Count == 0) return null;

            int n = pricePoints.Count;
            double dt = opt.QuarterMinutes / 60.0;                 // hours per quarter
            double chEff = Clamp(spec.ChargeEfficiency, 0.05, 1.0);
            double disEff = Clamp(spec.DischargeEfficiency, 0.05, 1.0);
            double cycleCost = Math.Max(0.0, opt.CycleCostEurPerKWh);
            double capacity = Math.Max(0.0, spec.CapacityKWh);

            // ── Per-quarter limits ───────────────────────────────────────────
            var maxChargeKWh = new double[n];      // AC-side energy that may be charged this quarter
            var maxDischargeKWh = new double[n];   // AC-side energy that may be delivered this quarter
            var minSoc = new double[n];
            var maxSoc = new double[n];

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
                minSoc[t] = mn;
                maxSoc[t] = mx;
            }

            // ── State per quarter ────────────────────────────────────────────
            var chargeKWh = new double[n];       // AC energy into the battery
            var dischargeKWh = new double[n];    // AC energy out of the battery
            var solarChargeKWh = new double[n];  // part of chargeKWh that came from solar surplus
            var importKWh = new double[n];       // grid import remaining after the battery
            var exportKWh = new double[n];       // grid export remaining after the battery
            var socEnd = new double[n];          // store level at the end of each quarter

            // ── 1. Baseline: self-consumption ────────────────────────────────
            double soc = Clamp(spec.InitialSocKWh, 0.0, capacity);

            for (int t = 0; t < n; t++)
            {
                double netLoadKWh = pricePoints[t].NetLoadWh / 1000.0;
                double surplus = netLoadKWh < 0.0 ? -netLoadKWh : 0.0;
                double deficit = netLoadKWh > 0.0 ? netLoadKWh : 0.0;

                if (surplus > Eps)
                {
                    // Store as much solar as the room and the charge limit allow.
                    double roomStore = Math.Max(0.0, maxSoc[t] - soc);
                    double absorb = Math.Min(surplus, Math.Min(roomStore / chEff, maxChargeKWh[t]));
                    if (absorb > Eps)
                    {
                        chargeKWh[t] += absorb;
                        solarChargeKWh[t] += absorb;
                        soc += absorb * chEff;
                    }
                    exportKWh[t] = surplus - absorb;
                }
                else if (deficit > Eps)
                {
                    // Cover the house from the battery, never below the reserve.
                    double availableStore = Math.Max(0.0, soc - minSoc[t]);
                    double deliver = Math.Min(deficit, Math.Min(availableStore * disEff, maxDischargeKWh[t]));
                    if (deliver > Eps)
                    {
                        dischargeKWh[t] += deliver;
                        soc -= deliver / disEff;
                    }
                    importKWh[t] = deficit - deliver;
                }

                soc = Clamp(soc, 0.0, capacity);
                socEnd[t] = soc;
            }

            // ── 2. Near-term hedge: earmark stock for the nearest profitable quarter ──
            // Runs once, before arbitrage, so the reserved energy is already committed and
            // cannot be bid away to a farther, higher-value peak. Disabled when
            // NearTermHedgeHours <= 0 (default) — behaviour is then unchanged.
            if (opt.NearTermHedgeHours > 0.0)
            {
                int cutIdx = Math.Min(n, (int)Math.Ceiling(opt.NearTermHedgeHours * 60.0 / opt.QuarterMinutes));

                // Best profit/kWh reachable anywhere in the horizon, and best reachable within
                // the window alone. Both use the same value rule as the arbitrage loop below.
                int bestGlobalJ = -1, bestWithinWindowJ = -1;
                double bestGlobalProfit = 0.0, bestWithinWindowProfit = 0.0;

                for (int j = 0; j < n; j++)
                {
                    double headroomJ = maxDischargeKWh[j] - dischargeKWh[j];
                    if (headroomJ <= Eps) continue;

                    double valueAtJ;
                    if (importKWh[j] > Eps)
                    {
                        valueAtJ = pricePoints[j].BuyEurPerKWh;
                    }
                    else
                    {
                        if (!opt.AllowExport) continue;
                        if (pricePoints[j].ReserveOnly) continue;
                        valueAtJ = pricePoints[j].SellEurPerKWh;
                    }

                    double profitAtJ = valueAtJ - cycleCost;
                    if (profitAtJ > bestGlobalProfit + Eps)
                    {
                        bestGlobalProfit = profitAtJ;
                        bestGlobalJ = j;
                    }
                    if (j < cutIdx && profitAtJ > bestWithinWindowProfit + Eps)
                    {
                        bestWithinWindowProfit = profitAtJ;
                        bestWithinWindowJ = j;
                    }
                }

                int hedgeJ = -1;
                double hedgeValueLimit = 0.0;

                for (int j = 0; j < cutIdx; j++)
                {
                    double dischargeHeadroomJ = maxDischargeKWh[j] - dischargeKWh[j];
                    if (dischargeHeadroomJ <= Eps) continue;

                    double valueJ;
                    double valueLimit;

                    if (importKWh[j] > Eps)
                    {
                        valueJ = pricePoints[j].BuyEurPerKWh;       // avoided import
                        valueLimit = importKWh[j];
                    }
                    else
                    {
                        if (!opt.AllowExport) continue;             // self-consumption: never export
                        if (pricePoints[j].ReserveOnly) continue;   // no export on predicted quarters
                        valueJ = pricePoints[j].SellEurPerKWh;      // exported
                        valueLimit = double.MaxValue;
                    }

                    if (valueJ - cycleCost <= Eps) continue;        // not sufficiently profitable

                    // Nearest profitable quarter wins the hedge slot — not the best one in the
                    // window. "Good enough and soon" beats "better and far away" here on purpose.
                    hedgeJ = j;
                    hedgeValueLimit = Math.Min(dischargeHeadroomJ, valueLimit);
                    break;
                }

                // Only actually commit the hedge when:
                //  1. the true best opportunity lies beyond the window (otherwise ordinary
                //     arbitrage will reach it on its own — nothing is at risk of indefinite
                //     deferral, so hedging would just give up profit for no reason), AND
                //  2. the nearest profitable quarter IS the best one reachable within the
                //     window (otherwise a clearly better, still-reachable quarter exists later
                //     in the same window — e.g. tonight's peak a few hours further out — and
                //     grabbing the first mediocre quarter would only cost profit for nothing,
                //     since that better nearby quarter isn't at risk either).
                bool bestOpportunityIsBeyondWindow = bestGlobalJ >= cutIdx;
                bool nearestIsAlsoBestWithinWindow = hedgeJ >= 0 && hedgeJ == bestWithinWindowJ;

                if (bestOpportunityIsBeyondWindow && nearestIsAlsoBestWithinWindow)
                {
                    double storeAvailable = Math.Max(0.0, spec.InitialSocKWh - minSoc[0]) * opt.NearTermHedgeFraction;

                    double block = Math.Min(hedgeValueLimit, storeAvailable * disEff);
                    double storeDelta = block / disEff;

                    // Draining the store at hedgeJ lowers the SOC path from there onward; must
                    // not cross the reserve on any later quarter.
                    double allowed = storeDelta;
                    for (int k = hedgeJ; k < n; k++)
                    {
                        double slack = socEnd[k] - minSoc[k];
                        if (slack < allowed) allowed = slack;
                        if (allowed <= Eps) break;
                    }
                    if (allowed < storeDelta)
                    {
                        storeDelta = allowed;
                        block = storeDelta * disEff;
                    }

                    if (block > Eps)
                    {
                        dischargeKWh[hedgeJ] += block;
                        if (importKWh[hedgeJ] > Eps)
                            importKWh[hedgeJ] = Math.Max(0.0, importKWh[hedgeJ] - block);

                        for (int k = hedgeJ; k < n; k++)
                            socEnd[k] -= storeDelta;
                    }
                }
            }

            // ── 3. Arbitrage: pair cheap charging with expensive discharging ──
            double roundTrip = chEff * disEff;

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                int bestI = NoSource, bestJ = -1;
                double bestProfitPerKWh = 0.0;
                double bestBlock = 0.0;

                for (int j = 1; j < n; j++)
                {
                    // What is one more kWh delivered at j worth?
                    double dischargeHeadroom = maxDischargeKWh[j] - dischargeKWh[j];
                    if (dischargeHeadroom <= Eps) continue;

                    double valueJ;
                    double valueLimit;   // how much energy is worth exactly valueJ

                    if (importKWh[j] > Eps)
                    {
                        valueJ = pricePoints[j].BuyEurPerKWh;   // avoided import
                        valueLimit = importKWh[j];
                    }
                    else
                    {
                        if (!opt.AllowExport) continue;                // self-consumption: never export
                        if (pricePoints[j].ReserveOnly) continue;      // no export on predicted quarters
                        valueJ = pricePoints[j].SellEurPerKWh;         // exported
                        valueLimit = double.MaxValue;
                    }

                    // ── Candidate A: discharge energy that is ALREADY in the battery ──
                    // The initial SOC was charged before this horizon (its cost is sunk), so
                    // exporting or consuming it only costs the cycle wear. Without this
                    // candidate the planner could never discharge energy stored before a
                    // replan: every discharge would need a paired charge inside the horizon.
                    // Feasibility: draining the store at j lowers the SOC path from j onward,
                    // which must stay at or above the reserve on every later quarter.
                    {
                        double profitPerKWh = valueJ - cycleCost;
                        if (profitPerKWh > bestProfitPerKWh + Eps)
                        {
                            double block = Math.Min(BlockKWh, Math.Min(dischargeHeadroom, valueLimit));
                            double storeDelta = block / disEff;
                            double allowed = storeDelta;
                            for (int k = j; k < n; k++)
                            {
                                double slack = socEnd[k] - minSoc[k];
                                if (slack < allowed) allowed = slack;
                                if (allowed <= Eps) break;
                            }
                            if (allowed > Eps)
                            {
                                if (allowed < storeDelta)
                                {
                                    storeDelta = allowed;
                                    block = storeDelta * disEff;
                                }
                                if (block > Eps)
                                {
                                    bestProfitPerKWh = profitPerKWh;
                                    bestI = StockSource;
                                    bestJ = j;
                                    bestBlock = block;
                                }
                            }
                        }
                    }

                    // ── Candidate B: charge at an earlier quarter i, discharge at j ──
                    for (int i = 0; i < j; i++)
                    {
                        if (pricePoints[i].ReserveOnly) continue;      // no grid charging on predicted quarters

                        double chargeHeadroom = maxChargeKWh[i] - chargeKWh[i];
                        if (chargeHeadroom <= Eps) continue;

                        // What does one more kWh of AC charge at i cost?
                        double costI;
                        double costLimit;

                        if (exportKWh[i] > Eps)
                        {
                            costI = pricePoints[i].SellEurPerKWh;      // forgone export revenue
                            costLimit = exportKWh[i];
                        }
                        else
                        {
                            costI = pricePoints[i].BuyEurPerKWh;       // imported from grid
                            costLimit = double.MaxValue;
                        }

                        double profitPerKWh = valueJ - costI / roundTrip - cycleCost;
                        if (profitPerKWh <= bestProfitPerKWh + Eps) continue;

                        // Feasible block size, expressed in kWh delivered at j.
                        double block = BlockKWh;
                        block = Math.Min(block, dischargeHeadroom);
                        block = Math.Min(block, valueLimit);
                        block = Math.Min(block, chargeHeadroom * roundTrip);
                        block = Math.Min(block, costLimit * roundTrip);

                        // Raising the SOC by block/disEff across (i, j] must not exceed maxSoc.
                        double storeDelta = block / disEff;
                        double allowed = storeDelta;
                        for (int k = i; k < j; k++)
                        {
                            double room = maxSoc[k] - socEnd[k];
                            if (room < allowed) allowed = room;
                            if (allowed <= Eps) break;
                        }
                        if (allowed <= Eps) continue;

                        if (allowed < storeDelta)
                        {
                            storeDelta = allowed;
                            block = storeDelta * disEff;
                        }
                        if (block <= Eps) continue;

                        bestProfitPerKWh = profitPerKWh;
                        bestI = i;
                        bestJ = j;
                        bestBlock = block;
                    }
                }

                if (bestI == NoSource || bestBlock <= Eps) break;   // nothing profitable left

                // Allocate the block.
                double deliver = bestBlock;
                double store = deliver / disEff;            // store drained at j

                if (bestI == StockSource)
                {
                    // Discharge from the initial stock: no charge quarter involved.
                    // The SOC path from bestJ onward drops by the drained store.
                    dischargeKWh[bestJ] += deliver;
                    if (importKWh[bestJ] > Eps)
                        importKWh[bestJ] = Math.Max(0.0, importKWh[bestJ] - deliver);

                    for (int k = bestJ; k < n; k++)
                        socEnd[k] -= store;

                    continue;
                }

                double acCharge = store / chEff;            // AC energy needed at i

                chargeKWh[bestI] += acCharge;
                if (exportKWh[bestI] > Eps)
                {
                    double fromSolar = Math.Min(acCharge, exportKWh[bestI]);
                    solarChargeKWh[bestI] += fromSolar;
                    exportKWh[bestI] -= fromSolar;
                }

                dischargeKWh[bestJ] += deliver;
                if (importKWh[bestJ] > Eps)
                    importKWh[bestJ] = Math.Max(0.0, importKWh[bestJ] - deliver);

                for (int k = bestI; k < bestJ; k++)
                    socEnd[k] += store;
            }

            // ── 3. Rebuild the SOC path and classify ─────────────────────────
            var plan = new List<PlanStep>(n);
            double objective = 0.0;
            soc = Clamp(spec.InitialSocKWh, 0.0, capacity);

            for (int t = 0; t < n; t++)
            {
                double socStart = soc;
                soc = Clamp(soc + chargeKWh[t] * chEff - dischargeKWh[t] / disEff, 0.0, capacity);

                double gridChargeKWh = Math.Max(0.0, chargeKWh[t] - solarChargeKWh[t]);

                // Battery discharge that leaves the house = export.
                double netLoadKWh = pricePoints[t].NetLoadWh / 1000.0;
                double deficit = netLoadKWh > 0.0 ? netLoadKWh : 0.0;
                double batteryExportKWh = Math.Max(0.0, dischargeKWh[t] - deficit);

                // Grid-fed charging is an active Charge. Battery energy leaving the house is an
                // active Discharge. Storing solar or covering the house is ZeroNetHome — the
                // battery regulates itself there, no open-loop setpoint needed.
                ActionMode mode =
                    gridChargeKWh > Eps ? ActionMode.Charge :
                    batteryExportKWh > Eps ? ActionMode.Discharge :
                    ActionMode.ZeroNetHome;

                double totalImport = importKWh[t] + gridChargeKWh;
                double totalExport = exportKWh[t] + batteryExportKWh;

                objective += totalExport * pricePoints[t].SellEurPerKWh
                           - totalImport * pricePoints[t].BuyEurPerKWh
                           - dischargeKWh[t] * cycleCost;

                plan.Add(new PlanStep(
                    pricePoints[t].Start,
                    mode,
                    ChargeKW: chargeKWh[t] / dt,
                    DischargeKW: dischargeKWh[t] / dt,
                    SocStartKWh: socStart,
                    SocEndKWh: soc));
            }

            return new PlanResult(true, objective, plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}