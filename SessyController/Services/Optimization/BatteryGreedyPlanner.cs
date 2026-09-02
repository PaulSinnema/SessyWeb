﻿using SessyController.Services.Items;

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
        /// <summary>
        /// Energy allocated per arbitrage iteration (kWh delivered at the discharge quarter).
        ///
        /// Pure speed knob, not an accuracy one: profitPerKWh does not depend on the block size and
        /// every limit is clamped before the block is allocated, so a coarser step reaches the same
        /// allocation in fewer iterations. Replayed over 75 real days the plans are bit-identical
        /// from 0.05 through 1.00 kWh while the solve time scales inversely — 0.05 took 22 s,
        /// 1.00 took 2.2 s. 0.20 is deliberately not the fastest value that measured identical:
        /// summer price curves are not proof for every curve, so it keeps a factor five in hand.
        /// </summary>
        private const double BlockKWh = 0.20;

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

        /// <param name="trace">
        /// Diagnostic sink, normally null. When set, the planner reports per quarter why it did not
        /// sell there — see <see cref="ExplainWhyNotSold"/>. Null costs nothing: that step is skipped
        /// entirely.
        /// </param>
        public static PlanResult? Solve(
            IReadOnlyList<PricePoint> pricePoints,
            BatterySpec spec,
            SessyOptions opt,
            IReadOnlyList<SocBound> socBounds,
            Action<string>? trace = null)
        {
            if (pricePoints == null || pricePoints.Count == 0) return null;

            var ctx = BuildContext(pricePoints, spec, opt, socBounds);
            var state = RunBaselinePass(ctx);
            var scratch = RunArbitragePass(ctx, state, out var lastFilledScratch);

            if (trace != null)
                ExplainWhyNotSold(ctx, state, lastFilledScratch, trace);

            return BuildPlanAndClassify(ctx, state);
        }

        // ══════════════════════════════════════════════════════════════════
        // Setup
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Everything the solve needs that does not change once computed: per-quarter limits,
        /// the efficiency/taper/capability curves and the small helper functions built on top of
        /// them. Bundled here so every phase below takes one parameter instead of a dozen.
        /// </summary>
        private sealed class Context
        {
            public required IReadOnlyList<PricePoint> PricePoints { get; init; }
            public required BatterySpec Spec { get; init; }
            public required SessyOptions Opt { get; init; }
            public required int N { get; init; }
            public required double Dt { get; init; }
            public required double CycleCost { get; init; }
            public required double Capacity { get; init; }
            public required EfficiencyCurve Efficiency { get; init; }

            /// <summary>
            /// A kWh sold out of existing stock has to be cycled back in later to replace it, at
            /// full power since no particular charge quarter is involved. That later cycle wears
            /// the battery too, so the wear cost of a stock discharge must cover 1 / roundTrip
            /// cycle's worth of wear, not just one — see Candidate A.
            /// </summary>
            public required double ReplacementRoundTrip { get; init; }

            public required double[] MaxChargeKWh { get; init; }      // AC-side energy chargeable this quarter
            public required double[] MaxDischargeKWh { get; init; }   // AC-side energy deliverable this quarter
            public required double[] MinSoc { get; init; }
            public required double[] MaxSoc { get; init; }

            /// <summary>
            /// Suffix maximum of <see cref="MinSoc"/>: the highest reserve still to come from t to
            /// the end of the horizon. The reserve is not monotonic — the bridge reserve at the last
            /// known-price quarter can jump far above the quarters around it — so discharging down
            /// to the current quarter's reserve alone can leave the battery below a higher reserve
            /// that arrives later. Every discharge must respect this suffix maximum instead.
            /// </summary>
            public required double[] MinSocFrom { get; init; }
            public required ChargeTaper Taper { get; init; }
            public required ChargeCapabilityFloor ChargeFloor { get; init; }
            public required DischargeCapability DischargeCapability { get; init; }

            /// <summary>
            /// Continuous future-value discount — see the class doc comment for why this replaced
            /// a discrete near-term-hedge pass. DiscountAt[t] = 1 / (1 + rate * hoursFromNow(t)).
            /// </summary>
            public required double[] DiscountAt { get; init; }

            /// <summary>Efficiency at the power a quarter would run at if it held this much AC energy.</summary>
            public double ChEffFor(double acKWh) => Efficiency.ChargeAt(Math.Max(0.0, acKWh) / Dt);

            /// <summary>Efficiency at the power a quarter would run at if it held this much AC energy.</summary>
            public double DisEffFor(double acKWh) => Efficiency.DischargeAt(Math.Max(0.0, acKWh) / Dt);

            /// <summary>
            /// Charge power tapers as the battery fills (CC/CV), so the cap cannot be a constant
            /// per quarter: it depends on the SOC the plan has reached by then. Evaluated against
            /// the SOC at the START of the quarter, which is the last committed value and does not
            /// change while the block being considered is allocated.
            ///
            /// The taper is fitted on a ratio and can only use quarters that recorded an untapered
            /// request, which is a small and one-sided slice of the history. The floor is what the
            /// bank has actually accepted at that state of charge, so the plan never assumes less
            /// than the hardware has already shown — see ChargeCapabilityFloor.
            /// </summary>
            public double TaperedChargeKWh(int t, double socStartKWh)
            {
                double cap = MaxChargeKWh[t];
                if (Capacity <= 0.0) return cap;

                double socFraction = socStartKWh / Capacity;

                double tapered = cap;
                if (Taper.Samples > 0)
                {
                    // Unknown temperature → the taper's reference, so the SOC term still applies.
                    double temp = PricePoints[t].TemperatureC ?? ChargeTaper.RefTemperatureC;
                    double mean48h = PricePoints[t].Temperature48hC ?? temp;

                    double ratio = Taper.Ratio(socFraction, temp, mean48h);
                    tapered = Math.Min(cap, Math.Max(0.0, Spec.MaxChargeKW) * ratio * Dt);
                }

                double floorKWh = ChargeFloor.PowerW(socFraction) / 1000.0 * Dt;

                // The floor lifts the prediction, never past what this quarter may take anyway.
                return Math.Min(cap, Math.Max(tapered, floorKWh));
            }

            /// <summary>
            /// Deliverable power falls off at a low state of charge (low cell voltage buys less
            /// power at the same current limit), so the discharge cap depends on the SOC the plan
            /// has reached, exactly like the charge taper. The shape is a plateau with a knee
            /// rather than a slope — see DischargeCapability.
            /// </summary>
            public double CappedDischargeKWh(int t, double socStartKWh)
            {
                double cap = MaxDischargeKWh[t];
                if (DischargeCapability.Samples == 0 || Capacity <= 0.0) return cap;

                double deliverableKWh = DischargeCapability.PowerW(socStartKWh / Capacity) / 1000.0 * Dt;
                return Math.Min(cap, deliverableKWh);
            }
        }

        /// <summary>
        /// Computes every per-quarter limit and curve lookup once, up front, so the rest of the
        /// solve only ever reads them.
        /// </summary>
        private static Context BuildContext(
            IReadOnlyList<PricePoint> pricePoints,
            BatterySpec spec,
            SessyOptions opt,
            IReadOnlyList<SocBound> socBounds)
        {
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

            var maxChargeKWh = new double[n];
            var maxDischargeKWh = new double[n];
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

            // Suffix maximum of the reserve: the highest reserve still to come from each quarter
            // to the end of the horizon. The baseline pass must not drain below this, or a later
            // (higher) reserve — e.g. the bridge reserve at the last known-price quarter — would
            // find the battery already too low to satisfy it.
            var minSocFrom = new double[n];
            for (int t = n - 1; t >= 0; t--)
                minSocFrom[t] = t == n - 1 ? minSoc[t] : Math.Max(minSoc[t], minSocFrom[t + 1]);

            double replacementRoundTrip = efficiency.ChargeAt(Math.Max(0.1, spec.MaxChargeKW))
                                        * efficiency.DischargeAt(Math.Max(0.1, spec.MaxDischargeKW));

            var discountAt = new double[n + 1];
            for (int t = 0; t <= n; t++)
                discountAt[t] = 1.0 / (1.0 + opt.FutureValueDiscountPerHour * t * dt);

            return new Context
            {
                PricePoints = pricePoints,
                Spec = spec,
                Opt = opt,
                N = n,
                Dt = dt,
                CycleCost = cycleCost,
                Capacity = capacity,
                Efficiency = efficiency,
                ReplacementRoundTrip = replacementRoundTrip,
                MaxChargeKWh = maxChargeKWh,
                MaxDischargeKWh = maxDischargeKWh,
                MinSoc = minSoc,
                MinSocFrom = minSocFrom,
                MaxSoc = maxSoc,
                Taper = spec.ChargeTaper ?? ChargeTaper.None,
                ChargeFloor = spec.ChargeFloor ?? ChargeCapabilityFloor.None,
                DischargeCapability = spec.DischargeCapability ?? DischargeCapability.None,
                DiscountAt = discountAt
            };
        }

        // ══════════════════════════════════════════════════════════════════
        // Per-quarter mutable state, shared by every phase below
        // ══════════════════════════════════════════════════════════════════

        /// <summary>The plan being built: how much is charged/discharged where, and the resulting SOC path.</summary>
        private sealed class State
        {
            public double[] ChargeKWh { get; }       // AC energy into the battery
            public double[] DischargeKWh { get; }    // AC energy out of the battery
            public double[] SolarChargeKWh { get; }  // part of ChargeKWh that came from solar surplus
            public double[] ImportKWh { get; }       // grid import remaining after the battery
            public double[] ExportKWh { get; }       // grid export remaining after the battery
            public double[] SocEnd { get; }          // store level at the end of each quarter

            public State(int n)
            {
                ChargeKWh = new double[n];
                DischargeKWh = new double[n];
                SolarChargeKWh = new double[n];
                ImportKWh = new double[n];
                ExportKWh = new double[n];
                SocEnd = new double[n];
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // 1. Baseline: self-consumption
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Solar surplus charges the battery, household deficit is served from the battery, both
        /// within SOC bounds and power limits. Whatever remains is exported / imported. This is
        /// the ZeroNetHome behaviour that arbitrage (phase 2) then layers trades on top of.
        /// </summary>
        private static State RunBaselinePass(Context ctx)
        {
            var state = new State(ctx.N);
            double soc = Clamp(ctx.Spec.InitialSocKWh, 0.0, ctx.Capacity);

            for (int t = 0; t < ctx.N; t++)
            {
                double netLoadKWh = ctx.PricePoints[t].NetLoadWh / 1000.0;
                double surplus = netLoadKWh < 0.0 ? -netLoadKWh : 0.0;
                double deficit = netLoadKWh > 0.0 ? netLoadKWh : 0.0;

                if (surplus > Eps)
                {
                    // Store as much solar as the room and the charge limit allow. The efficiency is
                    // read at the power this quarter would run at, so the same surplus stores less
                    // when it trickles in than when it arrives in bulk.
                    double roomStore = Math.Max(0.0, ctx.MaxSoc[t] - soc);
                    double surplusEff = ctx.ChEffFor(surplus);
                    double absorb = Math.Min(surplus, Math.Min(roomStore / surplusEff, ctx.TaperedChargeKWh(t, soc)));
                    if (absorb > Eps)
                    {
                        state.ChargeKWh[t] += absorb;
                        state.SolarChargeKWh[t] += absorb;
                        soc += absorb * ctx.ChEffFor(absorb);
                    }
                    state.ExportKWh[t] = surplus - absorb;
                }
                else if (deficit > Eps)
                {
                    // Cover the house from the battery, never below the highest reserve still to
                    // come. The reserve is not monotonic — the bridge reserve at the last
                    // known-price quarter can jump above the quarters around it — so draining to
                    // the current quarter's reserve alone can leave the battery below a higher
                    // reserve that arrives later, which then blocks all stock sales for the day.
                    double availableStore = Math.Max(0.0, soc - ctx.MinSocFrom[t]);
                    double deficitEff = ctx.DisEffFor(deficit);
                    double deliver = Math.Min(deficit, Math.Min(availableStore * deficitEff, ctx.CappedDischargeKWh(t, soc)));
                    if (deliver > Eps)
                    {
                        state.DischargeKWh[t] += deliver;
                        soc -= deliver / ctx.DisEffFor(deliver);
                    }
                    state.ImportKWh[t] = deficit - deliver;
                }

                soc = Clamp(soc, 0.0, ctx.Capacity);
                state.SocEnd[t] = soc;
            }

            return state;
        }

        // ══════════════════════════════════════════════════════════════════
        // 2. Arbitrage: pair cheap charging with expensive discharging
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Per-iteration scratch: everything that depends only on an index and on the current SOC
        /// path, which does not move while one block is being chosen. Filled once per arbitrage
        /// iteration instead of inside the pair loops — see the note on why that matters below.
        /// </summary>
        private sealed class Scratch
        {
            public double[] SocAtStart { get; }   // SOC at the start of each quarter
            public double[] ChargeCap { get; }    // AC kWh the quarter may still take, taper included
            public double[] ChEffCap { get; }     // charge efficiency at that cap
            public double[] DisCap { get; }       // AC kWh the quarter may still deliver
            public double[] DisEffCap { get; }    // discharge efficiency at that cap
            public double[] Slack { get; }        // socEnd - minSoc, room to drain
            public double[] Room { get; }         // maxSoc - socEnd, room to fill
            public double[] MinSlackFrom { get; } // suffix minimum of slack, for Candidate A
            public double[] MinRoomFrom { get; }  // suffix minimum of room, for Candidate C
            public double[] RoomMinTo { get; }    // per j: minimum of room over [i, j), for Candidate B

            public Scratch(int n)
            {
                SocAtStart = new double[n];
                ChargeCap = new double[n];
                ChEffCap = new double[n];
                DisCap = new double[n];
                DisEffCap = new double[n];
                Slack = new double[n];
                Room = new double[n];
                MinSlackFrom = new double[n];
                MinRoomFrom = new double[n];
                RoomMinTo = new double[n];
            }
        }

        /// <summary>The best profitable (charge, discharge) pairing found so far in one arbitrage iteration.</summary>
        private sealed class Candidate
        {
            public int I = NoSource;
            public int J = -1;
            public double Block;
            public double ProfitPerKWh;
            public bool IsRebuy;   // Candidate D: the charge quarter comes AFTER the discharge

            public bool Found => I != NoSource;
        }

        /// <summary>
        /// Recomputes everything in <see cref="Scratch"/> for the current SOC path. Filling this
        /// once per iteration — rather than inside the pair loops — is what keeps the search
        /// quadratic in the horizon instead of cubic: 3.5 s per solve at 72 hours became
        /// noticeably worse before this, and the production NAS is slower still.
        /// </summary>
        private static void FillScratch(Context ctx, State state, Scratch scratch)
        {
            for (int t = 0; t < ctx.N; t++)
            {
                scratch.SocAtStart[t] = t == 0 ? Clamp(ctx.Spec.InitialSocKWh, 0.0, ctx.Capacity) : state.SocEnd[t - 1];
                scratch.ChargeCap[t] = ctx.TaperedChargeKWh(t, scratch.SocAtStart[t]);
                scratch.ChEffCap[t] = ctx.ChEffFor(scratch.ChargeCap[t]);
                scratch.DisCap[t] = ctx.CappedDischargeKWh(t, scratch.SocAtStart[t]);
                scratch.DisEffCap[t] = ctx.DisEffFor(scratch.DisCap[t]);
                scratch.Slack[t] = state.SocEnd[t] - ctx.MinSoc[t];
                scratch.Room[t] = ctx.MaxSoc[t] - state.SocEnd[t];
            }

            for (int t = ctx.N - 1; t >= 0; t--)
            {
                scratch.MinSlackFrom[t] = t == ctx.N - 1 ? scratch.Slack[t] : Math.Min(scratch.Slack[t], scratch.MinSlackFrom[t + 1]);
                scratch.MinRoomFrom[t] = t == ctx.N - 1 ? scratch.Room[t] : Math.Min(scratch.Room[t], scratch.MinRoomFrom[t + 1]);
            }
        }

        /// <summary>
        /// What one more kWh delivered at j is worth: the avoided import price while a deficit
        /// remains there, otherwise the export price (only when export is allowed at j). Returns
        /// false when discharging at j cannot be valued at all, in which case j must be skipped
        /// entirely — including Candidates A, B and D.
        /// </summary>
        private static bool TryGetDischargeValue(Context ctx, State state, int j, out double valueJ, out double valueLimit)
        {
            if (state.ImportKWh[j] > Eps)
            {
                valueJ = ctx.PricePoints[j].BuyEurPerKWh;   // avoided import
                valueLimit = state.ImportKWh[j];
            }
            else
            {
                if (!ctx.Opt.AllowExport || ctx.PricePoints[j].ReserveOnly)
                {
                    valueJ = 0.0;
                    valueLimit = 0.0;
                    return false;   // self-consumption, or no export on predicted quarters
                }
                valueJ = ctx.PricePoints[j].SellEurPerKWh;  // exported
                valueLimit = double.MaxValue;
            }
            valueJ *= ctx.DiscountAt[j];
            return true;
        }

        /// <summary>
        /// Discharge energy that is ALREADY in the battery. This energy was charged before the
        /// horizon began. Its purchase price is sunk and deliberately plays no part in choosing
        /// *when* to discharge — that would be a sunk-cost error and would reject genuinely good
        /// trades.
        ///
        /// The floor here is the wear cost of replacing this discharge later, at full power,
        /// divided by that replacement cycle's round trip — see <see cref="Context.ReplacementRoundTrip"/>.
        /// Anything sold above that is a genuine gain over not touching the battery.
        ///
        /// Feasibility: draining the store at j lowers the SOC path from j onward, which must
        /// stay at or above the reserve on every later quarter.
        /// </summary>
        private static void TryCandidateA(
            Context ctx, Scratch scratch, int j, double valueJ, double dischargeHeadroom, double valueLimit, Candidate best)
        {
            double stockDisEff = scratch.DisEffCap[j];
            double profitPerKWh = valueJ - ctx.CycleCost / ctx.ReplacementRoundTrip;
            if (profitPerKWh <= best.ProfitPerKWh + Eps) return;

            double block = Math.Min(BlockKWh, Math.Min(dischargeHeadroom, valueLimit));
            double storeDelta = block / stockDisEff;
            double allowed = Math.Min(storeDelta, scratch.MinSlackFrom[j]);
            if (allowed <= Eps) return;

            if (allowed < storeDelta)
            {
                storeDelta = allowed;
                block = storeDelta * stockDisEff;
            }
            if (block <= Eps) return;

            best.ProfitPerKWh = profitPerKWh;
            best.I = StockSource;
            best.J = j;
            best.Block = block;
            best.IsRebuy = false;
        }

        /// <summary>
        /// Room left on the tightest quarter of [i, j), for every i &lt; j, in one backward pass.
        /// Candidate B needs that minimum per pair; scanning it per pair is what made the search
        /// cubic, so it is filled once per j instead.
        /// </summary>
        private static void FillRoomMinTo(Scratch scratch, int j)
        {
            for (int i = j - 1; i >= 0; i--)
                scratch.RoomMinTo[i] = i == j - 1 ? scratch.Room[i] : Math.Min(scratch.Room[i], scratch.RoomMinTo[i + 1]);
        }

        /// <summary>Charge at an earlier quarter i, discharge at j.</summary>
        private static void TryCandidateB(
            Context ctx, State state, Scratch scratch, int j, double valueJ, double dischargeHeadroom, double valueLimit, Candidate best)
        {
            for (int i = 0; i < j; i++)
            {
                if (ctx.PricePoints[i].ReserveOnly) continue;      // no grid charging on predicted quarters

                // Cap at i depends on the SOC the plan has reached by the start of i.
                double chargeHeadroom = scratch.ChargeCap[i] - state.ChargeKWh[i];
                if (chargeHeadroom <= Eps) continue;

                // What does one more kWh of AC charge at i cost?
                double costI;
                double costLimit;

                if (state.ExportKWh[i] > Eps)
                {
                    costI = ctx.PricePoints[i].SellEurPerKWh;      // forgone export revenue
                    costLimit = state.ExportKWh[i];
                }
                else
                {
                    costI = ctx.PricePoints[i].BuyEurPerKWh;       // imported from grid
                    costLimit = double.MaxValue;
                }
                costI *= ctx.DiscountAt[i];

                // Round trip of THIS pair, both sides read at the power the quarter can sustain
                // rather than at the sliver being placed — the decision uses the cap and the
                // bookkeeping further down (AllocateBlock) uses the fill.
                double pairRoundTrip = scratch.ChEffCap[i] * scratch.DisEffCap[j];
                double pairDisEff = scratch.DisEffCap[j];

                double profitPerKWh = valueJ - costI / pairRoundTrip - ctx.CycleCost;
                if (profitPerKWh <= best.ProfitPerKWh + Eps) continue;

                // Feasible block size, expressed in kWh delivered at j.
                double block = BlockKWh;
                block = Math.Min(block, dischargeHeadroom);
                block = Math.Min(block, valueLimit);
                block = Math.Min(block, chargeHeadroom * pairRoundTrip);
                block = Math.Min(block, costLimit * pairRoundTrip);

                // Raising the SOC by block/disEff across (i, j] must not exceed maxSoc.
                double storeDelta = block / pairDisEff;
                double allowed = Math.Min(storeDelta, scratch.RoomMinTo[i]);
                if (allowed <= Eps) continue;

                if (allowed < storeDelta)
                {
                    storeDelta = allowed;
                    block = storeDelta * pairDisEff;
                }
                if (block <= Eps) continue;

                best.ProfitPerKWh = profitPerKWh;
                best.I = i;
                best.J = j;
                best.Block = block;
                best.IsRebuy = false;
            }
        }

        /// <summary>
        /// Discharge at j now, buy it back at a later quarter k. The mirror image of Candidate B,
        /// and it has to exist for a reason that only shows up on real data: Candidate A may sell
        /// stock only while the SOC path stays above the reserve all the way to the end of the
        /// horizon. Energy the plan still needs tomorrow evening therefore cannot be sold tonight,
        /// however good tonight pays — on 06-08 that left the whole evening peak at €0,305
        /// untouched with 3,3 kWh in the battery, to be sold a day later after buying more at
        /// €0,16. Pairing the sale with its repurchase lifts the path back up after quarter k, so
        /// the trade becomes both visible and feasible.
        /// </summary>
        private static void TryCandidateD(
            Context ctx, State state, Scratch scratch, int j, double valueJ, double dischargeHeadroom, double valueLimit, Candidate best)
        {
            // Running minimum of the slack over [j, k), maintained as k ascends — the same value
            // an inner scan would otherwise have to recompute for every k.
            double dipSlack = double.MaxValue;

            for (int k = j + 1; k < ctx.N; k++)
            {
                dipSlack = Math.Min(dipSlack, scratch.Slack[k - 1]);

                if (ctx.PricePoints[k].ReserveOnly) continue;      // no grid charging on predicted quarters

                double rebuyHeadroom = scratch.ChargeCap[k] - state.ChargeKWh[k];
                if (rebuyHeadroom <= Eps) continue;

                double costK;
                double rebuyLimit;

                if (state.ExportKWh[k] > Eps)
                {
                    costK = ctx.PricePoints[k].SellEurPerKWh;      // forgone export revenue
                    rebuyLimit = state.ExportKWh[k];
                }
                else
                {
                    costK = ctx.PricePoints[k].BuyEurPerKWh;       // imported from grid
                    rebuyLimit = double.MaxValue;
                }
                costK *= ctx.DiscountAt[k];

                double rebuyRoundTrip = scratch.ChEffCap[k] * scratch.DisEffCap[j];
                double rebuyDisEff = scratch.DisEffCap[j];

                double rebuyProfit = valueJ - costK / rebuyRoundTrip - ctx.CycleCost;
                if (rebuyProfit <= best.ProfitPerKWh + Eps) continue;

                double rebuyBlock = BlockKWh;
                rebuyBlock = Math.Min(rebuyBlock, dischargeHeadroom);
                rebuyBlock = Math.Min(rebuyBlock, valueLimit);
                rebuyBlock = Math.Min(rebuyBlock, rebuyHeadroom * rebuyRoundTrip);
                rebuyBlock = Math.Min(rebuyBlock, rebuyLimit * rebuyRoundTrip);

                // The store dips between j and k and is level again afterwards, so only that
                // stretch has to stay above the reserve — which is exactly why this pairing frees
                // energy that Candidate A cannot touch.
                double rebuyStore = rebuyBlock / rebuyDisEff;
                double rebuyAllowed = Math.Min(rebuyStore, dipSlack);
                if (rebuyAllowed <= Eps) continue;

                if (rebuyAllowed < rebuyStore)
                {
                    rebuyStore = rebuyAllowed;
                    rebuyBlock = rebuyStore * rebuyDisEff;
                }
                if (rebuyBlock <= Eps) continue;

                best.ProfitPerKWh = rebuyProfit;
                best.I = k;
                best.J = j;
                best.Block = rebuyBlock;
                best.IsRebuy = true;
            }
        }

        /// <summary>
        /// Charge at i and keep it past the end of the horizon. No discharge quarter: the energy
        /// is valued at what replacing it would cost. Both sides are divided by the round trip,
        /// because both are AC-side purchases of the same stored kWh — one now, one later — so
        /// this is simply "is buying now cheaper than buying later". The cycle cost cancels: the
        /// kWh is discharged once whichever day it was charged, so subtracting it here would
        /// double-count against Candidate B, which already carries it.
        ///
        /// The value is realized after the horizon, so it is discounted at the horizon's end.
        /// Discounting costI as well (as Candidate B does) would make a distant cheap quarter look
        /// cheaper still, which is backwards for an option whose whole payoff lies beyond the plan.
        /// </summary>
        private static void TryCandidateC(Context ctx, State state, Scratch scratch, Candidate best)
        {
            if (!ctx.Opt.AllowCarryForward || ctx.Opt.ReplacementCostEurPerKWh <= 0.0) return;

            double carryValue = ctx.Opt.ReplacementCostEurPerKWh * ctx.DiscountAt[ctx.N];

            for (int i = 0; i < ctx.N; i++)
            {
                if (ctx.PricePoints[i].ReserveOnly) continue;      // no grid charging on predicted quarters

                double chargeHeadroom = scratch.ChargeCap[i] - state.ChargeKWh[i];
                if (chargeHeadroom <= Eps) continue;

                double costI;
                double costLimit;

                if (state.ExportKWh[i] > Eps)
                {
                    costI = ctx.PricePoints[i].SellEurPerKWh;      // forgone export revenue
                    costLimit = state.ExportKWh[i];
                }
                else
                {
                    costI = ctx.PricePoints[i].BuyEurPerKWh;       // imported from grid
                    costLimit = double.MaxValue;
                }

                // Carry-forward has no discharge quarter inside the horizon, so the charge side is
                // priced at the power quarter i would run at and the discharge side at what a
                // well-planned future discharge would achieve.
                double carryRoundTrip = scratch.ChEffCap[i]
                                      * ctx.Efficiency.DischargeAt(Math.Max(0.1, ctx.Spec.MaxDischargeKW));

                double profitPerKWh = (carryValue - costI) / carryRoundTrip;
                if (profitPerKWh <= best.ProfitPerKWh + Eps) continue;

                double block = BlockKWh;
                block = Math.Min(block, chargeHeadroom * carryRoundTrip);
                block = Math.Min(block, costLimit * carryRoundTrip);

                // The SOC stays raised from i to the end of the horizon — nothing gives it back —
                // so every quarter from i onward must have room for it.
                double carryDisEff = ctx.Efficiency.DischargeAt(Math.Max(0.1, ctx.Spec.MaxDischargeKW));
                double storeDelta = block / carryDisEff;
                double allowed = Math.Min(storeDelta, scratch.MinRoomFrom[i]);
                if (allowed <= Eps) continue;

                if (allowed < storeDelta)
                {
                    storeDelta = allowed;
                    block = storeDelta * carryDisEff;
                }
                if (block <= Eps) continue;

                best.ProfitPerKWh = profitPerKWh;
                best.I = i;
                best.J = CarryTarget;
                best.Block = block;
                best.IsRebuy = false;
            }
        }

        /// <summary>
        /// One arbitrage iteration: evaluates Candidates A, B and D for every discharge quarter j,
        /// then Candidate C once for the whole horizon, and returns whichever pairing scored best.
        /// </summary>
        private static Candidate FindBestCandidate(Context ctx, State state, Scratch scratch)
        {
            var best = new Candidate();

            // j starts at 0: Candidate A discharges energy already in the battery and needs no
            // earlier charge quarter, so index 0 is a valid discharge target. Candidate B does
            // require i < j, but its inner loop simply doesn't run when j == 0.
            //
            // This must not be raised to 1. Index 0 is the CURRENT quarter, and the plan is
            // re-solved every quarter — barring index 0 from discharging would push the discharge
            // one quarter into the future on every solve, so it would never actually execute: the
            // plan looks correct for future quarters while the current quarter silently falls back
            // to the ZeroNetHome baseline, forever.
            for (int j = 0; j < ctx.N; j++)
            {
                double dischargeHeadroom = scratch.DisCap[j] - state.DischargeKWh[j];
                if (dischargeHeadroom <= Eps) continue;

                if (!TryGetDischargeValue(ctx, state, j, out double valueJ, out double valueLimit)) continue;

                TryCandidateA(ctx, scratch, j, valueJ, dischargeHeadroom, valueLimit, best);
                FillRoomMinTo(scratch, j);
                TryCandidateB(ctx, state, scratch, j, valueJ, dischargeHeadroom, valueLimit, best);
                TryCandidateD(ctx, state, scratch, j, valueJ, dischargeHeadroom, valueLimit, best);
            }

            TryCandidateC(ctx, state, scratch, best);

            return best;
        }

        /// <summary>
        /// Commits the winning candidate: moves the energy, updates the SOC path over exactly the
        /// stretch it affects, and leaves everything else untouched for the next iteration.
        /// </summary>
        private static void AllocateBlock(Context ctx, State state, Candidate best)
        {
            double deliver = best.Block;
            double allocDisEff = best.J == CarryTarget
                ? ctx.Efficiency.DischargeAt(Math.Max(0.1, ctx.Spec.MaxDischargeKW))
                : ctx.DisEffFor(state.DischargeKWh[best.J] + deliver);

            double store = deliver / allocDisEff;        // store drained at j

            if (best.I == StockSource)
            {
                // Discharge from the initial stock: no charge quarter involved.
                // The SOC path from best.J onward drops by the drained store.
                state.DischargeKWh[best.J] += deliver;
                if (state.ImportKWh[best.J] > Eps)
                    state.ImportKWh[best.J] = Math.Max(0.0, state.ImportKWh[best.J] - deliver);

                for (int k = best.J; k < ctx.N; k++)
                    state.SocEnd[k] -= store;

                return;
            }

            double acCharge = store / ctx.ChEffFor(state.ChargeKWh[best.I] + store);   // AC energy needed at i

            state.ChargeKWh[best.I] += acCharge;
            if (state.ExportKWh[best.I] > Eps)
            {
                double fromSolar = Math.Min(acCharge, state.ExportKWh[best.I]);
                state.SolarChargeKWh[best.I] += fromSolar;
                state.ExportKWh[best.I] -= fromSolar;
            }

            if (best.J == CarryTarget)
            {
                // Kept past the end of the horizon: the SOC stays raised all the way out.
                for (int k = best.I; k < ctx.N; k++)
                    state.SocEnd[k] += store;

                return;
            }

            state.DischargeKWh[best.J] += deliver;
            if (state.ImportKWh[best.J] > Eps)
                state.ImportKWh[best.J] = Math.Max(0.0, state.ImportKWh[best.J] - deliver);

            if (best.IsRebuy)
            {
                // Sold at best.J and bought back at best.I: the store dips in between and is level
                // again from best.I onward, which is what makes the sale feasible at all.
                for (int k = best.J; k < best.I; k++)
                    state.SocEnd[k] -= store;

                return;
            }

            for (int k = best.I; k < best.J; k++)
                state.SocEnd[k] += store;
        }

        /// <summary>
        /// Repeatedly finds the most profitable feasible pairing and allocates one block to it,
        /// until no profitable pairing remains or the safety valve trips. Returns the scratch
        /// state as filled for the final (non-allocating) iteration, so <see cref="ExplainWhyNotSold"/>
        /// can reuse every term the search itself last saw.
        /// </summary>
        private static Scratch RunArbitragePass(Context ctx, State state, out Scratch finalScratch)
        {
            var scratch = new Scratch(ctx.N);

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                FillScratch(ctx, state, scratch);

                var best = FindBestCandidate(ctx, state, scratch);
                if (!best.Found || best.Block <= Eps) break;   // nothing profitable left

                AllocateBlock(ctx, state, best);
            }

            finalScratch = scratch;
            return scratch;
        }

        // ══════════════════════════════════════════════════════════════════
        // 2b. Why the search stopped
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Only called when a trace sink was given. "The battery ends the horizon with energy left
        /// while prices were high" is not answerable from the plan alone: the reason is per
        /// quarter, and it is one of three — the sale was not profitable, there was no room left to
        /// deliver it, or the SOC path could not go any lower without breaking the reserve.
        /// Reconstructing that afterwards is guesswork; this reuses the scratch state from the
        /// search's last iteration, where every term is still valid.
        /// </summary>
        private static void ExplainWhyNotSold(Context ctx, State state, Scratch scratch, Action<string> trace)
        {
            for (int j = 0; j < ctx.N; j++)
            {
                if (state.DischargeKWh[j] >= scratch.DisCap[j] - Eps) continue;   // quarter is already full

                bool avoidsImport = state.ImportKWh[j] > Eps;
                double rawValue = avoidsImport
                    ? ctx.PricePoints[j].BuyEurPerKWh
                    : ctx.PricePoints[j].SellEurPerKWh;

                if (!avoidsImport && (!ctx.Opt.AllowExport || ctx.PricePoints[j].ReserveOnly))
                {
                    trace($"{ctx.PricePoints[j].Start:dd-MM HH:mm} not sold: export not allowed here");
                    continue;
                }

                double value = rawValue * ctx.DiscountAt[j];
                double floor = ctx.CycleCost / ctx.ReplacementRoundTrip;
                double profit = value - floor;

                // Anything under this cannot carry a block worth allocating, so "profitable but not
                // taken" would be misleading — the store simply has nothing left to give between
                // here and the end of the horizon.
                const double UsefulSlackKWh = 0.01;

                string reason =
                    profit <= Eps ? $"value {value:F4} <= floor {floor:F4}"
                    : scratch.MinSlackFrom[j] < UsefulSlackKWh ? "SOC path has no room left above the reserve"
                    : $"PROFITABLE at {profit:F4} EUR/kWh but not taken";

                trace($"{ctx.PricePoints[j].Start:dd-MM HH:mm} not sold: {reason} " +
                      $"(value {value:F4}, floor {floor:F4}, slack {scratch.MinSlackFrom[j]:F4} kWh, " +
                      $"headroom {scratch.DisCap[j] - state.DischargeKWh[j]:F2} kWh)");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // 3. Rebuild the SOC path and classify
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Replays the allocation into an explicit SOC path, classifies every quarter as
        /// Charge / Discharge / ZeroNetHome, and accumulates the objective. Kept separate from the
        /// arbitrage search itself: the search decides with cap-based efficiencies (see the note
        /// in <see cref="Context.ChEffFor"/>), this reports what the plan really achieves.
        /// </summary>
        private static PlanResult BuildPlanAndClassify(Context ctx, State state)
        {
            var plan = new List<PlanStep>(ctx.N);
            double objective = 0.0;
            double soc = Clamp(ctx.Spec.InitialSocKWh, 0.0, ctx.Capacity);

            for (int t = 0; t < ctx.N; t++)
            {
                double socStart = soc;

                // Same efficiencies the allocation used, read at the power this quarter ended up
                // with — otherwise the SOC path drifts away from the objective it was chosen on.
                soc = Clamp(
                    soc + state.ChargeKWh[t] * ctx.ChEffFor(state.ChargeKWh[t])
                        - state.DischargeKWh[t] / ctx.DisEffFor(state.DischargeKWh[t]),
                    0.0, ctx.Capacity);

                double gridChargeKWh = Math.Max(0.0, state.ChargeKWh[t] - state.SolarChargeKWh[t]);

                // Battery discharge that leaves the house = export.
                double netLoadKWh = ctx.PricePoints[t].NetLoadWh / 1000.0;
                double deficit = netLoadKWh > 0.0 ? netLoadKWh : 0.0;
                double batteryExportKWh = Math.Max(0.0, state.DischargeKWh[t] - deficit);

                // Grid-fed charging is an active Charge. Battery energy leaving the house is an
                // active Discharge. Storing solar or covering the house is ZeroNetHome — the
                // battery regulates itself there, no open-loop setpoint needed.
                ActionMode mode =
                    gridChargeKWh > Eps ? ActionMode.Charge :
                    batteryExportKWh > Eps ? ActionMode.Discharge :
                    ActionMode.ZeroNetHome;

                double totalImport = state.ImportKWh[t] + gridChargeKWh;
                double totalExport = state.ExportKWh[t] + batteryExportKWh;

                objective += totalExport * ctx.PricePoints[t].SellEurPerKWh
                           - totalImport * ctx.PricePoints[t].BuyEurPerKWh
                           - state.DischargeKWh[t] * ctx.CycleCost;

                // What to ASK the batteries for. The allocation above is what the taper lets
                // through, and that is the right number for the SOC path — but not for the
                // setpoint: the batteries throttle themselves, so a request below their limit
                // only guarantees we stay under it. Where the taper was the binding cap, ask for
                // the untapered limit; where the allocation stopped on its own (nothing
                // profitable left to place), the allocation IS the request.
                double capKWh = ctx.TaperedChargeKWh(t, socStart);
                double nameplateKWh = Math.Max(0.0, ctx.Spec.MaxChargeKW) * ctx.Dt;
                double requestedChargeKWh = state.ChargeKWh[t] > Eps && state.ChargeKWh[t] >= capKWh - Eps
                    ? Math.Max(state.ChargeKWh[t], nameplateKWh)
                    : state.ChargeKWh[t];

                // Same on the way out. Sending the capped number was worse than merely modest:
                // the measured capability was reconstructed as plan / ratio, so every ratio
                // reproduced itself and the discharge throttle could never recover.
                double disCapKWh = ctx.CappedDischargeKWh(t, socStart);
                double disNameplateKWh = Math.Max(0.0, ctx.Spec.MaxDischargeKW) * ctx.Dt;
                double requestedDischargeKWh = state.DischargeKWh[t] > Eps && state.DischargeKWh[t] >= disCapKWh - Eps
                    ? Math.Max(state.DischargeKWh[t], disNameplateKWh)
                    : state.DischargeKWh[t];

                plan.Add(new PlanStep(
                    ctx.PricePoints[t].Start,
                    mode,
                    ChargeKW: state.ChargeKWh[t] / ctx.Dt,
                    DischargeKW: state.DischargeKWh[t] / ctx.Dt,
                    SocStartKWh: socStart,
                    SocEndKWh: soc,
                    RequestedChargeKW: requestedChargeKWh / ctx.Dt,
                    RequestedDischargeKW: requestedDischargeKWh / ctx.Dt));
            }

            // Energy left in the battery is worth what buying it again would cost. Without this
            // the objective would score every carry-forward block as a pure loss and report a
            // plan as worse than the one it beats. Only when carry-forward is on, so the reported
            // objective is unchanged for callers that do not use it.
            if (ctx.Opt.AllowCarryForward && ctx.Opt.ReplacementCostEurPerKWh > 0.0)
                objective += soc / ctx.Efficiency.ChargeAt(Math.Max(0.1, ctx.Spec.MaxChargeKW))
                           * ctx.Opt.ReplacementCostEurPerKWh;

            return new PlanResult(true, objective, plan);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : (v > max ? max : v);
    }
}
