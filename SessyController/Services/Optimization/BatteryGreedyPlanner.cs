using SessyController.Services.Items;

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
    ///     A third candidate exists when SessyOptions.AllowCarryForward is set: charge at i and
    ///     keep the energy past the end of the horizon, valued at the measured replacement cost.
    ///     Without it the planner can hold stock but can never acquire stock for beyond the
    ///     horizon, because the pair above needs a discharge quarter inside it — exactly the case
    ///     that matters when prices go negative. The cycle cost does not enter this comparison:
    ///     the kWh is cycled once whichever day it is charged, so it cancels.
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

        /// <summary>Sentinel target: the energy is kept past the end of the horizon, not discharged.</summary>
        private const int CarryTarget = -3;

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

            // Efficiency is not a constant: a large part of the conversion loss is fixed overhead,
            // so moving the same energy in fewer, fuller quarters keeps more of it (measured:
            // 0.80 below 1 kW against 0.92 at 4-5 kW). Without this the planner sees spreading as
            // free and has no reason to concentrate. A null curve — or one fitted on too little
            // data — is flat, and then every formula below reduces to the constant it used before.
            var efficiency = spec.Efficiency ?? EfficiencyCurve.Flat(chEff, disEff);

            // Efficiency at the power a quarter would run at if it held this much AC energy.
            double chEffFor(double acKWh) => efficiency.ChargeAt(Math.Max(0.0, acKWh) / dt);
            double disEffFor(double acKWh) => efficiency.DischargeAt(Math.Max(0.0, acKWh) / dt);

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

            // Charge power tapers as the battery fills (CC/CV), so the cap cannot be a constant
            // per quarter: it depends on the SOC the plan has reached by then. Evaluated against
            // the SOC at the START of the quarter, which is the last committed value and does not
            // change while the block being considered is allocated.
            var taper = spec.ChargeTaper ?? ChargeTaper.None;
            double taperedChargeKWh(int t, double socStartKWh)
            {
                double cap = maxChargeKWh[t];
                if (taper.Samples == 0 || capacity <= 0.0) return cap;

                // Unknown temperature → the taper's reference, so the SOC term still applies.
                double temp = pricePoints[t].TemperatureC ?? ChargeTaper.RefTemperatureC;
                double mean48h = pricePoints[t].Temperature48hC ?? temp;

                double ratio = taper.Ratio(socStartKWh / capacity, temp, mean48h);
                return Math.Min(cap, Math.Max(0.0, spec.MaxChargeKW) * ratio * dt);
            }

            // ── State per quarter ────────────────────────────────────────────
            var chargeKWh = new double[n];       // AC energy into the battery
            var dischargeKWh = new double[n];    // AC energy out of the battery
            var solarChargeKWh = new double[n];  // part of chargeKWh that came from solar surplus
            var importKWh = new double[n];       // grid import remaining after the battery
            var exportKWh = new double[n];       // grid export remaining after the battery
            var socEnd = new double[n];          // store level at the end of each quarter

            // SOC the plan has reached by the start of a quarter — the last committed value.
            double socStartAt(int t) => t == 0 ? Clamp(spec.InitialSocKWh, 0.0, capacity) : socEnd[t - 1];

            // Deliverable power falls off at a low state of charge (low cell voltage buys less
            // power at the same current limit), so the discharge cap depends on the SOC the plan
            // has reached, exactly like the charge taper. The shape is a plateau with a knee
            // rather than a slope — see DischargeCapability.
            var dischargeCapability = spec.DischargeCapability ?? DischargeCapability.None;
            double cappedDischargeKWh(int t, double socStartKWh)
            {
                double cap = maxDischargeKWh[t];
                if (dischargeCapability.Samples == 0 || capacity <= 0.0) return cap;

                double deliverableKWh = dischargeCapability.PowerW(socStartKWh / capacity) / 1000.0 * dt;
                return Math.Min(cap, deliverableKWh);
            }

            // ── 1. Baseline: self-consumption ────────────────────────────────
            double soc = Clamp(spec.InitialSocKWh, 0.0, capacity);

            for (int t = 0; t < n; t++)
            {
                double netLoadKWh = pricePoints[t].NetLoadWh / 1000.0;
                double surplus = netLoadKWh < 0.0 ? -netLoadKWh : 0.0;
                double deficit = netLoadKWh > 0.0 ? netLoadKWh : 0.0;

                if (surplus > Eps)
                {
                    // Store as much solar as the room and the charge limit allow. The efficiency is
                    // read at the power this quarter would run at, so the same surplus stores less
                    // when it trickles in than when it arrives in bulk.
                    double roomStore = Math.Max(0.0, maxSoc[t] - soc);
                    double surplusEff = chEffFor(surplus);
                    double absorb = Math.Min(surplus, Math.Min(roomStore / surplusEff, taperedChargeKWh(t, soc)));
                    if (absorb > Eps)
                    {
                        chargeKWh[t] += absorb;
                        solarChargeKWh[t] += absorb;
                        soc += absorb * chEffFor(absorb);
                    }
                    exportKWh[t] = surplus - absorb;
                }
                else if (deficit > Eps)
                {
                    // Cover the house from the battery, never below the reserve.
                    double availableStore = Math.Max(0.0, soc - minSoc[t]);
                    double deficitEff = disEffFor(deficit);
                    double deliver = Math.Min(deficit, Math.Min(availableStore * deficitEff, cappedDischargeKWh(t, soc)));
                    if (deliver > Eps)
                    {
                        dischargeKWh[t] += deliver;
                        soc -= deliver / disEffFor(deliver);
                    }
                    importKWh[t] = deficit - deliver;
                }

                soc = Clamp(soc, 0.0, capacity);
                socEnd[t] = soc;
            }

            // ── 2. Arbitrage: pair cheap charging with expensive discharging ──

            // For DECISIONS the efficiency is read at the power a quarter can sustain, not at the
            // sliver being placed: the question is whether this is a trickle quarter or a bulk one,
            // and a single 0.1 kWh block looks identical in both. A quarter throttled to 1 kW keeps
            // 0.88 of what goes in where one that takes 5 kW keeps 0.96, and half a cent of price
            // difference does not cover that.
            //
            // Moving the energy uses the fill-based values further down instead, so the SOC path
            // and the objective report what the plan really achieves. The decision is a forecast,
            // the bookkeeping is the outcome, and they are allowed to differ.
            double chEffAtCapacity(int i, double socStartKWh) => chEffFor(taperedChargeKWh(i, socStartKWh));
            double disEffAtCapacity(int j, double socStartKWh) => disEffFor(cappedDischargeKWh(j, socStartKWh));

            double roundTripAtCapacity(int i, int j, double socStartI, double socStartJ)
                => chEffAtCapacity(i, socStartI) * disEffAtCapacity(j, socStartJ);

            // No charge quarter is involved when energy is replaced from outside the horizon, so
            // the replacement is priced at what a well-planned purchase would achieve: full power.
            double replacementRoundTrip = efficiency.ChargeAt(Math.Max(0.1, spec.MaxChargeKW))
                                        * efficiency.DischargeAt(Math.Max(0.1, spec.MaxDischargeKW));

            // Continuous future-value discount — see the class doc comment for why this replaced
            // a discrete near-term-hedge pass. hoursFromNow(j) = j * dt since index 0 is "now".
            // FutureValueDiscountPerHour = 0 makes this 1.0 everywhere (no behaviour change).
            double Discount(int idx) => 1.0 / (1.0 + opt.FutureValueDiscountPerHour * idx * dt);

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                int bestI = NoSource, bestJ = -1;
                double bestProfitPerKWh = 0.0;
                double bestBlock = 0.0;
                bool bestIsRebuy = false;   // Candidate D: the charge quarter comes AFTER the discharge

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
                    double socStartJ = socStartAt(j);
                    double dischargeHeadroom = cappedDischargeKWh(j, socStartJ) - dischargeKWh[j];
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
                    // The replacement cost enters as a floor, for a different reason: a kWh still
                    // in the battery at the end of the horizon is worth zero in the objective,
                    // so without it the planner prefers dumping at any price above cycleCost
                    // over carrying energy forward. Charging what a replacement kWh will cost
                    // against each stock discharge prices that carry-forward option back in.
                    //
                    // Divided by the round trip for the same reason Candidate B divides costI:
                    // replacing one delivered kWh means buying 1 / roundTrip kWh on the AC side.
                    // It plays exactly the part of a charge quarter beyond the end of the horizon.
                    //
                    // Feasibility: draining the store at j lowers the SOC path from j onward,
                    // which must stay at or above the reserve on every later quarter.
                    {
                        double stockDisEff = disEffAtCapacity(j, socStartJ);
                        double profitPerKWh = valueJ - opt.ReplacementCostEurPerKWh / replacementRoundTrip - cycleCost;
                        if (profitPerKWh > bestProfitPerKWh + Eps)
                        {
                            double block = Math.Min(BlockKWh, Math.Min(dischargeHeadroom, valueLimit));
                            double storeDelta = block / stockDisEff;
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
                                    block = storeDelta * stockDisEff;
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

                        // Cap at i depends on the SOC the plan has reached by the start of i.
                        double socStartI = socStartAt(i);
                        double chargeHeadroom = taperedChargeKWh(i, socStartI) - chargeKWh[i];
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

                        // Round trip of THIS pair at the power both quarters would run at with the
                        // block added. A quarter that already carries energy converts better, so
                        // filling one up beats opening another — which is the whole point.
                        double pairRoundTrip = roundTripAtCapacity(i, j, socStartI, socStartJ);
                        double pairDisEff = disEffAtCapacity(j, socStartJ);

                        double profitPerKWh = valueJ - costI / pairRoundTrip - cycleCost;
                        if (profitPerKWh <= bestProfitPerKWh + Eps) continue;

                        // Feasible block size, expressed in kWh delivered at j.
                        double block = BlockKWh;
                        block = Math.Min(block, dischargeHeadroom);
                        block = Math.Min(block, valueLimit);
                        block = Math.Min(block, chargeHeadroom * pairRoundTrip);
                        block = Math.Min(block, costLimit * pairRoundTrip);

                        // Raising the SOC by block/disEff across (i, j] must not exceed maxSoc.
                        double storeDelta = block / pairDisEff;
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
                            block = storeDelta * pairDisEff;
                        }
                        if (block <= Eps) continue;

                        bestProfitPerKWh = profitPerKWh;
                        bestI = i;
                        bestJ = j;
                        bestBlock = block;
                        bestIsRebuy = false;
                    }

                    // ── Candidate D: discharge at j now, buy it back at a later quarter k ──
                    // The mirror image of Candidate B, and it has to exist for a reason that only
                    // shows up on real data: Candidate A may sell stock only while the SOC path
                    // stays above the reserve all the way to the end of the horizon. Energy the
                    // plan still needs tomorrow evening therefore cannot be sold tonight, however
                    // good tonight pays — on 06-08 that left the whole evening peak at €0,305
                    // untouched with 3,3 kWh in the battery, to be sold a day later after buying
                    // more at €0,16. Pairing the sale with its repurchase lifts the path back up
                    // after quarter k, so the trade becomes both visible and feasible.
                    for (int k = j + 1; k < n; k++)
                    {
                        if (pricePoints[k].ReserveOnly) continue;      // no grid charging on predicted quarters

                        double socStartK = socStartAt(k);
                        double rebuyHeadroom = taperedChargeKWh(k, socStartK) - chargeKWh[k];
                        if (rebuyHeadroom <= Eps) continue;

                        double costK;
                        double rebuyLimit;

                        if (exportKWh[k] > Eps)
                        {
                            costK = pricePoints[k].SellEurPerKWh;      // forgone export revenue
                            rebuyLimit = exportKWh[k];
                        }
                        else
                        {
                            costK = pricePoints[k].BuyEurPerKWh;       // imported from grid
                            rebuyLimit = double.MaxValue;
                        }
                        costK *= Discount(k);

                        double rebuyRoundTrip = chEffAtCapacity(k, socStartK) * disEffAtCapacity(j, socStartJ);
                        double rebuyDisEff = disEffAtCapacity(j, socStartJ);

                        double rebuyProfit = valueJ - costK / rebuyRoundTrip - cycleCost;
                        if (rebuyProfit <= bestProfitPerKWh + Eps) continue;

                        double rebuyBlock = BlockKWh;
                        rebuyBlock = Math.Min(rebuyBlock, dischargeHeadroom);
                        rebuyBlock = Math.Min(rebuyBlock, valueLimit);
                        rebuyBlock = Math.Min(rebuyBlock, rebuyHeadroom * rebuyRoundTrip);
                        rebuyBlock = Math.Min(rebuyBlock, rebuyLimit * rebuyRoundTrip);

                        // The store dips between j and k and is level again afterwards, so only
                        // that stretch has to stay above the reserve — which is exactly why this
                        // pairing frees energy that Candidate A cannot touch.
                        double rebuyStore = rebuyBlock / rebuyDisEff;
                        double rebuyAllowed = rebuyStore;
                        for (int m = j; m < k; m++)
                        {
                            double slack = socEnd[m] - minSoc[m];
                            if (slack < rebuyAllowed) rebuyAllowed = slack;
                            if (rebuyAllowed <= Eps) break;
                        }
                        if (rebuyAllowed <= Eps) continue;

                        if (rebuyAllowed < rebuyStore)
                        {
                            rebuyStore = rebuyAllowed;
                            rebuyBlock = rebuyStore * rebuyDisEff;
                        }
                        if (rebuyBlock <= Eps) continue;

                        bestProfitPerKWh = rebuyProfit;
                        bestI = k;
                        bestJ = j;
                        bestBlock = rebuyBlock;
                        bestIsRebuy = true;
                    }
                }

                // ── Candidate C: charge at i and keep it past the end of the horizon ──
                // No discharge quarter: the energy is valued at what replacing it would cost.
                // Both sides are divided by the round trip, because both are AC-side purchases
                // of the same stored kWh — one now, one later — so this is simply "is buying
                // now cheaper than buying later". The cycle cost cancels: the kWh is discharged
                // once whichever day it was charged, so subtracting it here would double-count
                // against Candidate B, which already carries it.
                //
                // The value is realized after the horizon, so it is discounted at the horizon's
                // end. Discounting costI as well (as Candidate B does) would make a distant
                // cheap quarter look cheaper still, which is backwards for an option whose whole
                // payoff lies beyond the plan.
                if (opt.AllowCarryForward && opt.ReplacementCostEurPerKWh > 0.0)
                {
                    double carryValue = opt.ReplacementCostEurPerKWh * Discount(n);

                    for (int i = 0; i < n; i++)
                    {
                        if (pricePoints[i].ReserveOnly) continue;      // no grid charging on predicted quarters

                        double socStartI = socStartAt(i);
                        double chargeHeadroom = taperedChargeKWh(i, socStartI) - chargeKWh[i];
                        if (chargeHeadroom <= Eps) continue;

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

                        // Carry-forward has no discharge quarter inside the horizon, so the charge
                        // side is priced at the power quarter i would run at and the discharge side
                        // at what a well-planned future discharge would achieve.
                        double carryRoundTrip = chEffAtCapacity(i, socStartI)
                                              * efficiency.DischargeAt(Math.Max(0.1, spec.MaxDischargeKW));

                        double profitPerKWh = (carryValue - costI) / carryRoundTrip;
                        if (profitPerKWh <= bestProfitPerKWh + Eps) continue;

                        double block = BlockKWh;
                        block = Math.Min(block, chargeHeadroom * carryRoundTrip);
                        block = Math.Min(block, costLimit * carryRoundTrip);

                        // The SOC stays raised from i to the end of the horizon — nothing gives
                        // it back — so every quarter from i onward must have room for it.
                        double carryDisEff = efficiency.DischargeAt(Math.Max(0.1, spec.MaxDischargeKW));
                        double storeDelta = block / carryDisEff;
                        double allowed = storeDelta;
                        for (int k = i; k < n; k++)
                        {
                            double room = maxSoc[k] - socEnd[k];
                            if (room < allowed) allowed = room;
                            if (allowed <= Eps) break;
                        }
                        if (allowed <= Eps) continue;

                        if (allowed < storeDelta)
                        {
                            storeDelta = allowed;
                            block = storeDelta * carryDisEff;
                        }
                        if (block <= Eps) continue;

                        bestProfitPerKWh = profitPerKWh;
                        bestI = i;
                        bestJ = CarryTarget;
                        bestBlock = block;
                    }
                }

                if (bestI == NoSource || bestBlock <= Eps) break;   // nothing profitable left

                // Allocate the block, at the efficiencies of the quarters it actually lands in.
                double deliver = bestBlock;
                double allocDisEff = bestJ == CarryTarget
                    ? efficiency.DischargeAt(Math.Max(0.1, spec.MaxDischargeKW))
                    : disEffFor(dischargeKWh[bestJ] + deliver);

                double store = deliver / allocDisEff;        // store drained at j

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

                double acCharge = store / chEffFor(chargeKWh[bestI] + store);   // AC energy needed at i

                chargeKWh[bestI] += acCharge;
                if (exportKWh[bestI] > Eps)
                {
                    double fromSolar = Math.Min(acCharge, exportKWh[bestI]);
                    solarChargeKWh[bestI] += fromSolar;
                    exportKWh[bestI] -= fromSolar;
                }

                if (bestJ == CarryTarget)
                {
                    // Kept past the end of the horizon: the SOC stays raised all the way out.
                    for (int k = bestI; k < n; k++)
                        socEnd[k] += store;

                    continue;
                }

                dischargeKWh[bestJ] += deliver;
                if (importKWh[bestJ] > Eps)
                    importKWh[bestJ] = Math.Max(0.0, importKWh[bestJ] - deliver);

                if (bestIsRebuy)
                {
                    // Sold at bestJ and bought back at bestI: the store dips in between and is
                    // level again from bestI onward, which is what makes the sale feasible at all.
                    for (int k = bestJ; k < bestI; k++)
                        socEnd[k] -= store;

                    continue;
                }

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

                // Same efficiencies the allocation used, read at the power this quarter ended up
                // with — otherwise the SOC path drifts away from the objective it was chosen on.
                soc = Clamp(
                    soc + chargeKWh[t] * chEffFor(chargeKWh[t]) - dischargeKWh[t] / disEffFor(dischargeKWh[t]),
                    0.0, capacity);

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

                // What to ASK the batteries for. The allocation above is what the taper lets
                // through, and that is the right number for the SOC path — but not for the
                // setpoint: the batteries throttle themselves, so a request below their limit
                // only guarantees we stay under it. Where the taper was the binding cap, ask for
                // the untapered limit; where the allocation stopped on its own (nothing
                // profitable left to place), the allocation IS the request.
                double capKWh = taperedChargeKWh(t, socStart);
                double nameplateKWh = Math.Max(0.0, spec.MaxChargeKW) * dt;
                double requestedChargeKWh = chargeKWh[t] > Eps && chargeKWh[t] >= capKWh - Eps
                    ? Math.Max(chargeKWh[t], nameplateKWh)
                    : chargeKWh[t];

                // Same on the way out. Sending the capped number was worse than merely modest:
                // the measured capability was reconstructed as plan / ratio, so every ratio
                // reproduced itself and the discharge throttle could never recover.
                double disCapKWh = cappedDischargeKWh(t, socStart);
                double disNameplateKWh = Math.Max(0.0, spec.MaxDischargeKW) * dt;
                double requestedDischargeKWh = dischargeKWh[t] > Eps && dischargeKWh[t] >= disCapKWh - Eps
                    ? Math.Max(dischargeKWh[t], disNameplateKWh)
                    : dischargeKWh[t];

                plan.Add(new PlanStep(
                    pricePoints[t].Start,
                    mode,
                    ChargeKW: chargeKWh[t] / dt,
                    DischargeKW: dischargeKWh[t] / dt,
                    SocStartKWh: socStart,
                    SocEndKWh: soc,
                    RequestedChargeKW: requestedChargeKWh / dt,
                    RequestedDischargeKW: requestedDischargeKWh / dt));
            }

            // Energy left in the battery is worth what buying it again would cost. Without this
            // the objective would score every carry-forward block as a pure loss and report a
            // plan as worse than the one it beats. Only when carry-forward is on, so the reported
            // objective is unchanged for callers that do not use it.
            if (opt.AllowCarryForward && opt.ReplacementCostEurPerKWh > 0.0)
                objective += soc / efficiency.ChargeAt(Math.Max(0.1, spec.MaxChargeKW))
                           * opt.ReplacementCostEurPerKWh;

            return new PlanResult(true, objective, plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}