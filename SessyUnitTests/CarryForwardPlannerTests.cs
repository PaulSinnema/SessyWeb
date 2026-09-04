using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Covers the carry-forward option: the planner may charge for a payoff that lies past the end
    /// of its horizon, valued at the measured replacement cost.
    ///
    /// The behaviour being pinned down is asymmetric on purpose:
    ///   - with carry-forward off the horizon is a hard wall, and a cheap quarter with no sale
    ///     behind it is worth nothing (the old behaviour, which must not drift);
    ///   - with it on, "buy now or buy later" is the only comparison, and the cycle cost does not
    ///     enter it because the kWh is cycled once either way;
    ///   - stock already in the battery is never sold below the wear cost of the cycle needed to
    ///     replace it, and that floor carries the round-trip loss, exactly like the cost of a real
    ///     charge quarter — see BatteryGreedyPlanner.Context.ReplacementRoundTrip.
    /// </summary>
    public class CarryForwardPlannerTests
    {
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 7.0;
        private const double MaxDischargeKW = 5.1;
        private const double Efficiency = 0.95;
        private const double RoundTrip = Efficiency * Efficiency;

        /// <summary>One arbitrage block, the granularity every SOC assertion inherits.</summary>
        private const double BlockKWh = 0.1;

        private static readonly DateTime Start = new(2026, 7, 30, 12, 0, 0);

        /// <summary>Flat horizon: one price, no household load, no solar.</summary>
        private static List<PricePoint> FlatPrices(int quarters, double buy, double sell)
            => Enumerable.Range(0, quarters)
                .Select(i => new PricePoint(
                    Start.AddMinutes(15 * i),
                    BuyEurPerKWh: buy,
                    SellEurPerKWh: sell,
                    NetLoadWh: 0.0,
                    SolarSurplusWh: 0.0))
                .ToList();

        private static List<SocBound> Bounds(IEnumerable<PricePoint> points, double maxSocKWh = CapacityKWh)
            => points.Select(p => new SocBound(p.Start, 0.0, maxSocKWh)).ToList();

        private static BatterySpec Spec(double initialSocKWh)
            => new(CapacityKWh, initialSocKWh, MaxChargeKW, MaxDischargeKW, Efficiency, Efficiency);

        private static SessyOptions Options(
            double replacementCost = 0.0,
            bool allowCarryForward = false,
            double cycleCost = 0.0)
            => new(QuarterMinutes: 15,
                   CycleCostEurPerKWh: cycleCost,
                   AllowExport: true,
                   FutureValueDiscountPerHour: 0.0,
                   ReservationPriceEurPerKWh: replacementCost,
                   AllowCarryForward: allowCarryForward);

        private static double FinalSoc(PlanResult result) => result.Plan[^1].SocEndKWh;

        // ── Carry-forward off ─────────────────────────────────────────────────

        [Fact]
        public void CarryForwardOff_DoesNotChargeForAPayoffOutsideTheHorizon()
        {
            var points = FlatPrices(8, buy: 0.05, sell: 0.05);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 0.0),
                Options(replacementCost: 0.20, allowCarryForward: false),
                Bounds(points));

            Assert.NotNull(result);
            Assert.True(FinalSoc(result!) <= BlockKWh,
                $"battery charged to {FinalSoc(result!):F3} kWh while carry-forward was off.");
        }

        // ── Carry-forward on ──────────────────────────────────────────────────

        [Fact]
        public void CarryForwardOn_ChargesWhenBuyingNowBeatsBuyingLater()
        {
            var points = FlatPrices(8, buy: 0.05, sell: 0.05);

            // A non-zero cycle cost keeps the stock floor (cycleCost / roundTrip = 0.1108) above
            // the 0.05 sale price in this fixture, so the charged stock is held rather than
            // immediately re-sold inside the horizon — isolating the thing being tested here.
            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 0.0),
                Options(replacementCost: 0.20, allowCarryForward: true, cycleCost: 0.10),
                Bounds(points));

            Assert.NotNull(result);

            // Eight quarters at 7 kW deliver 8 * 1.75 = 14 kWh AC, which stores 14 * 0.95 = 13.3.
            Assert.True(FinalSoc(result!) > 13.0,
                $"expected the cheap window to be used, final SOC was {FinalSoc(result!):F3} kWh.");

            // Nothing is sold: there is no discharge quarter worth anything inside the horizon.
            Assert.All(result!.Plan, step => Assert.True(step.DischargeKW <= 1e-6));
        }

        [Fact]
        public void CarryForwardOn_RefusesToChargeAboveTheReplacementCost()
        {
            var points = FlatPrices(8, buy: 0.30, sell: 0.30);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 0.0),
                Options(replacementCost: 0.20, allowCarryForward: true),
                Bounds(points));

            Assert.NotNull(result);
            Assert.True(FinalSoc(result!) <= BlockKWh,
                $"charged at 0.30 while a replacement kWh costs 0.20 (final SOC {FinalSoc(result!):F3} kWh).");
        }

        [Fact]
        public void CarryForwardOn_TakesOnlyTheQuartersCheaperThanReplacement()
        {
            // Buying costs more than a replacement kWh everywhere, and selling inside the horizon
            // pays too little to be worth a trade — so only carry-forward can act here.
            var points = FlatPrices(8, buy: 0.25, sell: 0.05);

            // One quarter goes negative: the case the whole feature exists for.
            points[3] = points[3] with { BuyEurPerKWh = -0.10 };

            // Same isolation as above: a non-zero cycle cost keeps the 0.05 sale price under the
            // stock floor, so the charged block is not immediately sold back inside the horizon.
            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 0.0),
                Options(replacementCost: 0.20, allowCarryForward: true, cycleCost: 0.10),
                Bounds(points));

            Assert.NotNull(result);

            // One quarter at 7 kW stores 7 * 0.25 * 0.95 = 1.6625 kWh, and nothing else qualifies.
            Assert.InRange(FinalSoc(result!), 1.6625 - BlockKWh, 1.6625 + BlockKWh);
            Assert.True(result!.Plan[3].ChargeKW > 0.0, "the negative-price quarter was not used.");

            for (int i = 0; i < result.Plan.Count; i++)
                if (i != 3)
                    Assert.True(result.Plan[i].ChargeKW <= 1e-6,
                        $"quarter {i} charged at 0.25 while a replacement kWh costs 0.20.");
        }

        [Fact]
        public void CarryForwardOn_StaysWithinTheMaximumSoc()
        {
            var points = FlatPrices(8, buy: 0.05, sell: 0.05);

            // Same isolation as CarryForwardOn_ChargesWhenBuyingNowBeatsBuyingLater.
            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 0.0),
                Options(replacementCost: 0.20, allowCarryForward: true, cycleCost: 0.10),
                Bounds(points, maxSocKWh: 5.0));

            Assert.NotNull(result);
            Assert.All(result!.Plan, step => Assert.True(step.SocEndKWh <= 5.0 + 1e-6,
                $"SOC {step.SocEndKWh:F3} kWh exceeded the 5.0 kWh bound."));
            Assert.True(FinalSoc(result) > 5.0 - BlockKWh, "the bound was not filled up to.");
        }

        [Fact]
        public void CarryForwardOn_CountsTheHeldEnergyInTheObjective()
        {
            var points = FlatPrices(8, buy: 0.05, sell: 0.05);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 0.0),
                Options(replacementCost: 0.20, allowCarryForward: true),
                Bounds(points));

            Assert.NotNull(result);

            // Buying at 0.05 what would otherwise cost 0.20 cannot score as a loss; without the
            // terminal value the objective would be the purchase price alone, deeply negative.
            Assert.True(result!.ObjectiveEur > 0.0,
                $"objective {result.ObjectiveEur:F2} ignores the energy still in the battery.");
        }

        // ── The floor under selling stock ─────────────────────────────────────

        [Fact]
        public void Stock_IsNotSoldBelowTheCostOfReplacingIt()
        {
            // 0.10 / roundTrip = 0.1108, so a sale at 0.10 is a loss once the round trip is paid.
            var points = FlatPrices(4, buy: 0.10, sell: 0.10);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 8.0),
                Options(cycleCost: 0.10),
                Bounds(points));

            Assert.NotNull(result);
            Assert.All(result!.Plan, step => Assert.True(step.DischargeKW <= 1e-6,
                "stock was sold below the cycle cost of replacing it, ignoring the round-trip loss."));
        }

        [Fact]
        public void Stock_IsSoldOnceThePriceClearsTheReplacementCost()
        {
            double floor = 0.10 / RoundTrip;
            var points = FlatPrices(4, buy: floor + 0.05, sell: floor + 0.05);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 8.0),
                Options(cycleCost: 0.10),
                Bounds(points));

            Assert.NotNull(result);
            Assert.True(result!.Plan.Any(step => step.DischargeKW > 0.0),
                "a sale comfortably above the replacement cost was refused.");
        }

        [Fact]
        public void ReplacementCostZero_LeavesTheOriginalBehaviourIntact()
        {
            var points = FlatPrices(4, buy: 0.02, sell: 0.02);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 8.0), Options(), Bounds(points));

            Assert.NotNull(result);

            // No floor and no carry-forward: any price above the (zero) cycle cost is worth taking.
            Assert.Contains(result!.Plan, step => step.DischargeKW > 0.0);
        }
    }
}
