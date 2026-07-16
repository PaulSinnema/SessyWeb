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
    ///  2. Arbitrage pass, in small energy blocks.
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
    ///     Both value[j] and cost[i] are scaled by a future-value discount factor before
    ///     comparison — see FutureValueDiscountPerHour below. This is the only place time
    ///     preference enters the model; the reported objective and the executed plan still use
    ///     full, undiscounted prices, so nothing is actually left on the table — the discount only
    ///     nudges which of several similarly-profitable quarters the search reaches for first.
    ///
    ///     Delivering E kWh at j drains E / dischargeEfficiency from the store, which in turn
    ///     needs E / (dischargeEfficiency * chargeEfficiency) kWh on the AC side at i. Hence
    ///
    ///        profit(E) = E * ( value[j]·discount(j) − cost[i]·discount(i) / (chargeEff * dischargeEff) − cycleCost )
    ///
    ///     A pair is feasible when the charge fits in i's remaining charge power, the discharge
    ///     fits in j's remaining discharge power, and raising the SOC across (i, j] keeps it at
    ///     or below the maximum SOC on every quarter in between.
    ///
    ///  3. Classification.
    ///     Charging fed by the grid → Charge. Discharging that exports → Discharge.
    ///     Everything else (storing solar, covering the house) → ZeroNetHome.
    ///
    /// Why a discount instead of a discrete "reserve for the near term" rule: an earlier version
    /// of this planner had a separate pass that earmarked stock for a nearby quarter before
    /// arbitrage ran. Every version of that rule needed a hand-picked selection heuristic (nearest
    /// profitable? best within a window? only if the window doesn't already contain the global
    /// best?) and each one had a different edge case where it grabbed the wrong quarter or didn't
    /// fire at all. A continuous per-hour discount removes the discrete rule entirely: it feeds
    /// into the exact same profit comparison arbitrage already makes for every candidate, so
    /// there is no separate pass, no window cutoff, and no selection heuristic to get wrong.
    /// A quarter a day away needs to be genuinely more profitable than one available tonight to
    /// still win the comparison — by exactly as much as FutureValueDiscountPerHour says a day of
    /// forecast uncertainty is worth. FutureValueDiscountPerHour = 0 (default) reproduces the
    /// original undiscounted behaviour exactly.
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

            // ── 2. Arbitrage: pair cheap charging with expensive discharging ──
            double roundTrip = chEff * disEff;

            // Continuous future-value discount — see the class doc comment for why this replaced
            // a discrete near-term-hedge pass. hoursFromNow(j) = j * dt since index 0 is "now".
            // FutureValueDiscountPerHour = 0 makes this 1.0 everywhere (no behaviour change).
            double Discount(int idx) => 1.0 / (1.0 + opt.FutureValueDiscountPerHour * idx * dt);

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                int bestI = NoSource, bestJ = -1;
                double bestProfitPerKWh = 0.0;
                double bestBlock = 0.0;

                // j starts at 0: Candidate A discharges energy already in the battery and needs
                // no earlier charge quarter, so index 0 is a valid discharge target. Candidate B
                // does require i < j, but its inner loop simply doesn't run when j == 0.
                //
                // This must not be raised to 1. Index 0 is the CURRENT quarter, and the plan is
                // re-solved every quarter — barring index 0 from discharging would push the
                // discharge one quarter into the future on every solve, so it would never
                // actually execute: the plan looks correct for future quarters while the current
                // quarter silently falls back to the ZeroNetHome baseline, forever.
                for (int j = 0; j < n; j++)
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
                    valueJ *= Discount(j);

                    // ── Candidate A: discharge energy that is ALREADY in the battery ──
                    // This energy was charged before the horizon began. Its purchase price is
                    // sunk and deliberately plays no part in choosing *when* to discharge —
                    // that would be a sunk-cost error and would reject genuinely good trades.
                    //
                    // StockCostEurPerKWh enters as a floor, for a different reason: a kWh still
                    // in the battery at the end of the horizon is worth zero in the objective,
                    // so without it the planner prefers dumping at any price above cycleCost
                    // over carrying energy forward. Charging the replacement cost against each
                    // stock discharge prices that carry-forward option back in. Solar energy has
                    // a cost basis of 0, so a solar-filled battery behaves exactly as before.
                    //
                    // Feasibility: draining the store at j lowers the SOC path from j onward,
                    // which must stay at or above the reserve on every later quarter.
                    {
                        double profitPerKWh = valueJ - opt.StockCostEurPerKWh - cycleCost;
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
                        costI *= Discount(i);

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