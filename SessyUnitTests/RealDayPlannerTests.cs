using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// The carry-forward and learning tests next door each isolate one mechanism on a flat,
    /// loadless, cost-free horizon. That is what makes them readable, but it also means none of
    /// them can catch what actually decides a real plan. This file closes those gaps with
    /// production numbers taken from the running system on 30-07-2026:
    ///
    ///   cycle cost      0.0862 EUR/kWh, from the three battery investments
    ///                   (3849 + 3662.50 + 3662.50) / (16.2 kWh * 8000 cycles)
    ///   efficiency      0.918 one-way, measured back out of the plan itself: a quarter planned
    ///                   at 4816 W raised the planned SOC by 1105 Wh
    ///   power           6600 W charge / 5100 W discharge nameplate (3 x 2200 / 3 x 1700)
    ///   throttle        the discharge quarters of that evening sat at 3207 W and 3056 W, so
    ///                   0.63 and 0.60 of nameplate
    ///   discount        0.01 per hour, what the learner wrote while it has no history yet
    ///   prices          the real all-in buy and sell curve, 11:30 to 23:45
    ///   forecast        the real consumption and solar forecast per quarter
    ///
    /// The reason this matters: on that day the plan peaked at 86.5% SOC and stopped charging,
    /// and the flat-horizon tests would happily have passed with a planner that charged to 100%.
    /// A zero cycle cost in particular hides the one claim that carry-forward rests on — that the
    /// cycle cost cancels out of a carry-forward decision but not out of an arbitrage decision.
    /// With cycleCost = 0 both branches behave identically and the claim is untested.
    /// </summary>
    public class RealDayPlannerTests
    {
        // ── Production constants ──────────────────────────────────────────────

        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 6.6;
        private const double MaxDischargeKW = 5.1;
        private const double Efficiency = 0.918;
        private const double RoundTrip = Efficiency * Efficiency;   // 0.843
        private const double CycleCost = 0.0862;
        private const double DiscountPerHour = 0.01;

        /// <summary>Measured replacement cost on 30-07: P25 of 31 daily cheapest all-in prices.</summary>
        private const double ReplacementCost = 0.1307;

        /// <summary>SOC at 11:30, back-calculated from the planned SOC and charge of that quarter.</summary>
        private const double StartSocKWh = 3.475;

        private static readonly DateTime Start = new(2026, 7, 30, 11, 30, 0);

        /// <summary>One real quarter: all-in prices, consumption forecast (W), solar forecast (Wh).</summary>
        private sealed record Quarter(int Index, double Buy, double Sell, double ConsumptionW, double SolarWh);

        /// <summary>
        /// The real horizon of 30-07-2026, 11:30 to 23:45, straight out of PlannedQuarters.
        /// Cheap until 14:15, then a long climb to the evening peak at 20:45.
        /// </summary>
        private static readonly Quarter[] RealDay =
        [
            new(0, 0.1631, 0.1190, 696, 500),
            new(1, 0.1516, 0.1076, 635, 500),
            new(2, 0.1516, 0.1075, 1908, 715),
            new(3, 0.1496, 0.1056, 1908, 715),
            new(4, 0.1600, 0.1159, 1908, 715),
            new(5, 0.1558, 0.1118, 1908, 715),
            new(6, 0.1438, 0.0997, 448, 538),
            new(7, 0.1615, 0.1175, 448, 538),
            new(8, 0.1728, 0.1287, 448, 538),
            new(9, 0.1690, 0.1250, 448, 538),
            new(10, 0.1551, 0.1110, 465, 249),
            new(11, 0.1673, 0.1232, 465, 249),
            new(12, 0.1778, 0.1338, 465, 249),
            new(13, 0.2270, 0.1829, 465, 249),
            new(14, 0.1945, 0.1505, 634, 128),
            new(15, 0.2198, 0.1758, 634, 128),
            new(16, 0.2392, 0.1952, 634, 128),
            new(17, 0.2608, 0.2167, 634, 128),
            new(18, 0.2361, 0.1921, 367, 69),
            new(19, 0.2464, 0.2023, 367, 69),
            new(20, 0.2623, 0.2183, 367, 69),
            new(21, 0.2853, 0.2412, 367, 69),
            new(22, 0.2726, 0.2286, 436, 20),
            new(23, 0.2709, 0.2269, 436, 20),
            new(24, 0.2898, 0.2457, 436, 20),
            new(25, 0.3167, 0.2726, 436, 20),
            new(26, 0.2878, 0.2438, 766, 86),
            new(27, 0.3069, 0.2628, 766, 86),
            new(28, 0.3244, 0.2803, 822, 86),
            new(29, 0.3277, 0.2837, 822, 86),
            new(30, 0.3131, 0.2691, 685, 63),
            new(31, 0.3409, 0.2969, 685, 63),
            new(32, 0.3526, 0.3086, 685, 63),
            new(33, 0.3641, 0.3201, 685, 63),
            new(34, 0.3542, 0.3101, 568, 12),
            new(35, 0.3607, 0.3167, 568, 12),
            new(36, 0.3731, 0.3290, 568, 12),
            new(37, 0.3956, 0.3516, 601, 12),
            new(38, 0.3920, 0.3479, 639, 0),
            new(39, 0.3872, 0.3432, 639, 0),
            new(40, 0.3786, 0.3346, 626, 0),
            new(41, 0.3633, 0.3192, 626, 0),
            new(42, 0.3764, 0.3324, 648, 0),
            new(43, 0.3677, 0.3236, 648, 0),
            new(44, 0.3594, 0.3154, 648, 0),
            new(45, 0.3489, 0.3048, 648, 0),
            new(46, 0.3522, 0.3081, 652, 0),
            new(47, 0.3417, 0.2977, 652, 0),
            new(48, 0.3383, 0.2942, 652, 0),
        ];

        /// <summary>
        /// Net load mirrors QuarterlyInfo.NetLoadWh: consumption is an average power over the
        /// quarter, solar is already an energy per quarter.
        /// </summary>
        private static List<PricePoint> RealPricePoints()
            => RealDay.Select(q =>
            {
                double netLoadWh = q.ConsumptionW * 0.25 - q.SolarWh;
                double surplusWh = netLoadWh < 0.0 ? -netLoadWh : 0.0;

                // The evening discharge quarters were throttled to 0.63 and 0.60 of nameplate;
                // charge quarters carried no bucket cap because the taper handles that side.
                double dischargeRatio = q.Index <= 37 ? 0.63 : 0.60;

                return new PricePoint(
                    Start.AddMinutes(15 * q.Index),
                    BuyEurPerKWh: q.Buy,
                    SellEurPerKWh: q.Sell,
                    NetLoadWh: netLoadWh,
                    SolarSurplusWh: surplusWh,
                    MaxChargeKW: null,
                    MaxDischargeKW: MaxDischargeKW * dischargeRatio);
            }).ToList();

        private static List<SocBound> RealBounds(double minSocKWh = 0.0)
            => RealDay.Select(q => new SocBound(
                Start.AddMinutes(15 * q.Index), minSocKWh, CapacityKWh)).ToList();

        private static BatterySpec RealSpec(ChargeTaper? taper = null)
            => new(CapacityKWh, StartSocKWh, MaxChargeKW, MaxDischargeKW, Efficiency, Efficiency, taper);

        private static SessyOptions RealOptions(
            double replacementCost = 0.0,
            bool allowCarryForward = false,
            double discountPerHour = DiscountPerHour,
            double cycleCost = CycleCost)
            => new(QuarterMinutes: 15,
                   CycleCostEurPerKWh: cycleCost,
                   AllowExport: true,
                   FutureValueDiscountPerHour: discountPerHour,
                   ReplacementCostEurPerKWh: replacementCost,
                   AllowCarryForward: allowCarryForward);

        // ── The day itself ────────────────────────────────────────────────────

        [Fact]
        public void RealDay_StopsChargingWellBelowFull()
        {
            var points = RealPricePoints();

            var result = BatteryGreedyPlanner.Solve(points, RealSpec(), RealOptions(), RealBounds());

            Assert.NotNull(result);

            double peakPct = result!.Plan.Max(step => step.SocEndKWh) / CapacityKWh * 100.0;

            // Not a bug and not a coincidence: charging at 0.1778 to sell at 0.3101 nets
            // 0.3101*0.9217 - 0.1778*0.9709/0.843 - 0.0862 = -0.005 EUR/kWh. The band is wide
            // because the exact peak depends on the block granularity, not on the economics.
            Assert.InRange(peakPct, 75.0, 95.0);
        }

        [Fact]
        public void RealDay_RefusesTheQuarterThatWouldPushItHigher()
        {
            var points = RealPricePoints();

            var result = BatteryGreedyPlanner.Solve(points, RealSpec(), RealOptions(), RealBounds());

            Assert.NotNull(result);

            // Quarter 12 is 14:30 at 0.1778 — the first quarter of the climb, and the cheapest one
            // the planner left unused. Charging there loses half a cent per kWh.
            //
            // The test is on the mode, not on ChargeKW: quarter 12 carries a solar surplus
            // (465 W * 0.25 - 249 Wh = -133 Wh), so the baseline stores that surplus and
            // ChargeKW is legitimately non-zero. Only ActionMode.Charge means grid-fed charging,
            // which is the decision under test.
            Assert.NotEqual(ActionMode.Charge, result!.Plan[12].Mode);
        }

        [Fact]
        public void RealDay_ChargesTheCheapWindowItDoesUse()
        {
            var points = RealPricePoints();

            var result = BatteryGreedyPlanner.Solve(points, RealSpec(), RealOptions(), RealBounds());

            Assert.NotNull(result);

            // Quarter 6 is the cheapest of the day at 0.1438; refusing that one would mean the
            // whole cheap window is being skipped, which is the failure the user reported.
            Assert.True(result!.Plan[6].ChargeKW > 0.0, "the cheapest quarter of the day went unused.");
        }

        [Fact]
        public void RealDay_SellsIntoTheHighestQuartersFirst()
        {
            var points = RealPricePoints();

            var result = BatteryGreedyPlanner.Solve(points, RealSpec(), RealOptions(), RealBounds());

            Assert.NotNull(result);

            var discharging = Enumerable.Range(0, RealDay.Length)
                .Where(i => result!.Plan[i].DischargeKW > 1e-6)
                .ToList();

            Assert.NotEmpty(discharging);

            // Every quarter it sells in must pay at least as much as every quarter it skips,
            // give or take the one partially filled block at the budget boundary.
            double worstUsed = discharging.Min(i => RealDay[i].Sell);
            var skippedBetter = Enumerable.Range(0, RealDay.Length)
                .Where(i => result!.Plan[i].DischargeKW <= 1e-6 && RealDay[i].Sell > worstUsed)
                .Where(i => result!.Plan[i].ChargeKW <= 1e-6)
                .ToList();

            Assert.True(skippedBetter.Count <= 1,
                $"skipped {skippedBetter.Count} quarters that pay more than one it used.");
        }

        [Fact]
        public void RealDay_HonoursTheNightReserve()
        {
            var points = RealPricePoints();

            // 4.5 kWh is 27.8% of capacity, what the plan actually held back that night. The floor
            // only applies from 19:00 onward: the day starts at 3.475 kWh, below the reserve, so a
            // floor over the whole horizon would be infeasible from the first quarter — which is
            // also why BuildSocBounds ramps it instead of imposing it flat.
            const double reserveKWh = 4.5;
            const int eveningFrom = 30;

            var bounds = RealDay
                .Select(q => new SocBound(
                    Start.AddMinutes(15 * q.Index),
                    q.Index >= eveningFrom ? reserveKWh : 0.0,
                    CapacityKWh))
                .ToList();

            var result = BatteryGreedyPlanner.Solve(points, RealSpec(), RealOptions(), bounds);

            Assert.NotNull(result);

            for (int i = eveningFrom; i < RealDay.Length; i++)
                Assert.True(result!.Plan[i].SocEndKWh >= reserveKWh - 1e-6,
                    $"quarter {i}: SOC {result.Plan[i].SocEndKWh:F3} kWh dropped below the {reserveKWh} kWh reserve.");
        }

        // ── Cycle cost: the claim carry-forward rests on ───────────────────────

        [Fact]
        public void CarryForward_DoesNotPayTheCycleCost_ArbitrageDoes()
        {
            // A price that beats the replacement cost by less than the cycle cost. Carrying is
            // worth it — the kWh is cycled once whichever day it was charged, so the cycle cost
            // is not part of the comparison. Selling it inside the horizon at the same price is
            // not worth it, because there the cycle cost is real.
            double buy = ReplacementCost - 0.02;                 // 0.1107
            var points = FlatDay(quarters: 8, buy: buy, sell: buy);

            var carry = BatteryGreedyPlanner.Solve(
                points, FlatSpec(0.0),
                RealOptions(replacementCost: ReplacementCost, allowCarryForward: true, discountPerHour: 0.0),
                FlatBounds(points));

            var arbitrageOnly = BatteryGreedyPlanner.Solve(
                points, FlatSpec(0.0),
                RealOptions(replacementCost: ReplacementCost, allowCarryForward: false, discountPerHour: 0.0),
                FlatBounds(points));

            Assert.NotNull(carry);
            Assert.NotNull(arbitrageOnly);

            Assert.True(carry!.Plan[^1].SocEndKWh > 1.0,
                "carry-forward refused a trade that only the cycle cost stands in the way of, "
                + "so the cycle cost is being subtracted where it should cancel.");

            Assert.True(arbitrageOnly!.Plan[^1].SocEndKWh <= 0.1,
                "arbitrage took a trade the cycle cost makes unprofitable.");
        }

        [Fact]
        public void StockFloor_AddsTheRoundTripLossOnTopOfTheCycleCost()
        {
            // Selling stock has to clear the cycle cost of the cycle needed to replace it, grossed
            // up for the round trip. Priced just under that floor, the sale must be refused.
            double floor = CycleCost / RoundTrip;                // 0.1023
            var points = FlatDay(8, buy: floor - 0.01, sell: floor - 0.01);

            var result = BatteryGreedyPlanner.Solve(
                points, FlatSpec(8.0),
                RealOptions(replacementCost: ReplacementCost, discountPerHour: 0.0),
                FlatBounds(points));

            Assert.NotNull(result);
            Assert.All(result!.Plan, step => Assert.True(step.DischargeKW <= 1e-6,
                "sold stock without covering the round-trip loss on top of the cycle cost."));
        }

        // ── The horizon discount on carry-forward ─────────────────────────────

        [Fact]
        public void CarryForward_IsSuppressedByTheHorizonDiscount()
        {
            // Pins today's behaviour so that changing it is a decision, not an accident.
            // Over this 49-quarter fixture at the learner's 1%/hour the replacement cost is
            // discounted to 0.1307 * 1/(1 + 0.01 * 12.25) = 0.1164, and over the real 36.5-hour
            // horizon to 0.0958 — both below every all-in price of that day, the cheapest quarter
            // being 0.1438. Carry-forward therefore cannot fire at all.
            var points = RealPricePoints();

            var result = BatteryGreedyPlanner.Solve(
                points, RealSpec(),
                RealOptions(replacementCost: ReplacementCost, allowCarryForward: true),
                RealBounds());

            var withoutCarry = BatteryGreedyPlanner.Solve(
                points, RealSpec(), RealOptions(replacementCost: ReplacementCost), RealBounds());

            Assert.NotNull(result);
            Assert.NotNull(withoutCarry);

            Assert.Equal(withoutCarry!.Plan[^1].SocEndKWh, result!.Plan[^1].SocEndKWh, 3);
        }

        [Fact]
        public void CarryForward_FiresOnceThePriceIsBelowTheDiscountedReplacementCost()
        {
            // Two things have to be true at once before carry-forward can do anything, and both
            // of them took a failing test to pin down.
            //
            // First, the price has to be below the DISCOUNTED replacement cost — one cheap
            // quarter is not enough on its own, because the evening peak can still absorb its
            // 1.65 kWh and selling beats holding.
            //
            // Second, and this is the constraint that is easy to miss: carrying energy past the
            // horizon means holding it THROUGH the peak, so the SOC has to stay below maximum on
            // every remaining quarter. Once arbitrage has filled the battery for the evening
            // there is no room left to carry anything, even though the battery is empty again
            // afterwards. Capacity is shared between the two, not additive.
            //
            // So the case where carry-forward earns its keep is a cheap window plus a peak that
            // cannot swallow it — here a battery throttled to 15% of nameplate on the discharge
            // side, which is what a hot battery room does.
            var points = RealDay.Select(q =>
            {
                double netLoadWh = q.ConsumptionW * 0.25 - q.SolarWh;
                double buy = q.Index <= 11 ? 0.05 : q.Buy;
                double sell = q.Index <= 11 ? 0.01 : q.Sell;

                return new PricePoint(
                    Start.AddMinutes(15 * q.Index),
                    BuyEurPerKWh: buy,
                    SellEurPerKWh: sell,
                    NetLoadWh: netLoadWh,
                    SolarSurplusWh: netLoadWh < 0.0 ? -netLoadWh : 0.0,
                    MaxChargeKW: null,
                    MaxDischargeKW: MaxDischargeKW * 0.15);
            }).ToList();

            var withCarry = BatteryGreedyPlanner.Solve(
                points, RealSpec(),
                RealOptions(replacementCost: ReplacementCost, allowCarryForward: true),
                RealBounds());

            var withoutCarry = BatteryGreedyPlanner.Solve(
                points, RealSpec(), RealOptions(replacementCost: ReplacementCost), RealBounds());

            Assert.NotNull(withCarry);
            Assert.NotNull(withoutCarry);

            Assert.True(withCarry!.Plan[^1].SocEndKWh > withoutCarry!.Plan[^1].SocEndKWh + 0.05,
                $"carry-forward added nothing: {withCarry.Plan[^1].SocEndKWh:F3} kWh left against "
                + $"{withoutCarry!.Plan[^1].SocEndKWh:F3} kWh without it.");
        }

        // ── Taper on the real temperatures of that day ─────────────────────────

        [Fact]
        public void Taper_ShortensTheChargeWindowAtTheMeasuredTemperature()
        {
            // 30-07 measured 21.8 C on average, 27.3 C peak, 23.6 C over the preceding 48 hours —
            // no heatwave. With the production coefficients that gives ratio = 0.957 - 0.535*soc
            // around 25 C, so about 0.51 at 83% SOC: 3.4 kW, above what the plan asked for there.
            var taper = new ChargeTaper(A: 1.054, B: 0.535, C: 0.0237, D: 0.0152, Samples: 312);

            var points = RealPricePoints()
                .Select(p => p with { TemperatureC = 25.0, Temperature48hC = 23.6 })
                .ToList();

            var tapered = BatteryGreedyPlanner.Solve(points, RealSpec(taper), RealOptions(), RealBounds());
            var flat = BatteryGreedyPlanner.Solve(points, RealSpec(), RealOptions(), RealBounds());

            Assert.NotNull(tapered);
            Assert.NotNull(flat);

            // The invariant is on the TOTAL, not per quarter. A lower cap in one quarter pushes
            // the greedy allocation into another, so a tapered plan can charge more in a given
            // quarter than the untapered one — asserting per quarter would be asserting the
            // allocation order, not the taper.
            double taperedKWh = tapered!.Plan.Sum(step => step.ChargeKW * 0.25);
            double flatKWh = flat!.Plan.Sum(step => step.ChargeKW * 0.25);

            Assert.True(taperedKWh <= flatKWh + 1e-6,
                $"taper raised total charge from {flatKWh:F3} to {taperedKWh:F3} kWh.");

            // And it must never plan above nameplate, taper or no taper.
            Assert.All(tapered.Plan, step => Assert.True(step.ChargeKW <= MaxChargeKW + 1e-6));
        }

        // ── Flat-horizon helpers, kept for the isolated cycle-cost cases ───────

        private static List<PricePoint> FlatDay(int quarters, double buy, double sell)
            => Enumerable.Range(0, quarters)
                .Select(i => new PricePoint(Start.AddMinutes(15 * i), buy, sell, 0.0, 0.0))
                .ToList();

        private static List<SocBound> FlatBounds(IEnumerable<PricePoint> points)
            => points.Select(p => new SocBound(p.Start, 0.0, CapacityKWh)).ToList();

        private static BatterySpec FlatSpec(double initialSocKWh)
            => new(CapacityKWh, initialSocKWh, MaxChargeKW, MaxDischargeKW, Efficiency, Efficiency);
    }
}
