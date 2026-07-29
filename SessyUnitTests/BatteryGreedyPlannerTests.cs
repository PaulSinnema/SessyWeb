using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Covers the two properties the planner needs in order to actually fill the battery during a
    /// cheap window.
    ///
    /// The first is the one that regressed: MilpServiceBase.BuildSocBounds used to hand the planner
    /// maxSoc = capacity − (that quarter's solar surplus). Because the arbitrage feasibility check
    /// takes the MINIMUM headroom over the whole span between the charge and the discharge quarter,
    /// a single sunny quarter in between capped every plan below full — on production data the plan
    /// SOC pinned at exactly capacity − surplus and never targeted 100%. These tests hand the
    /// planner maxSoc = capacity and assert it does reach it, surplus quarters in between included.
    /// They cover the planner side only; BuildSocBounds itself needs the full DI graph.
    /// </summary>
    public class BatteryGreedyPlannerTests
    {
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 7.0;
        private const double MaxDischargeKW = 5.1;
        private const double Efficiency = 0.95;

        private const int CheapQuarters = 8;

        // Enough expensive quarters to drain a full battery: 16.2 kWh of store leaves as
        // 16.2 * 0.95 = 15.4 kWh, and one quarter delivers 5.1 * 0.25 = 1.275 kWh, so 13 would
        // do. With fewer the planner correctly refuses to charge beyond what it can later sell,
        // and the test would measure the discharge window instead of the SOC ceiling.
        private const int ExpensiveQuarters = 16;

        private static readonly DateTime Start = new(2026, 7, 29, 12, 0, 0);

        /// <summary>
        /// Cheap quarters carry a solar surplus (negative net load), expensive ones a household
        /// deficit. <paramref name="surplusWh"/> is the surplus per cheap quarter.
        /// </summary>
        private static List<PricePoint> BuildPrices(double surplusWh)
        {
            var points = new List<PricePoint>();

            for (int i = 0; i < CheapQuarters; i++)
                points.Add(new PricePoint(
                    Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.10,
                    SellEurPerKWh: 0.10,
                    NetLoadWh: -surplusWh,
                    SolarSurplusWh: surplusWh));

            for (int i = 0; i < ExpensiveQuarters; i++)
                points.Add(new PricePoint(
                    Start.AddMinutes(15 * (CheapQuarters + i)),
                    BuyEurPerKWh: 0.60,
                    SellEurPerKWh: 0.60,
                    NetLoadWh: 1000.0,
                    SolarSurplusWh: 0.0));

            return points;
        }

        private static List<SocBound> FullRangeBounds(IEnumerable<PricePoint> points)
            => points.Select(p => new SocBound(p.Start, 0.0, CapacityKWh)).ToList();

        private static BatterySpec Spec(double initialSocKWh, ChargeTaper? taper = null)
            => new(CapacityKWh, initialSocKWh, MaxChargeKW, MaxDischargeKW, Efficiency, Efficiency, taper);

        private static SessyOptions Options()
            => new(QuarterMinutes: 15, CycleCostEurPerKWh: 0.0);

        [Theory]
        [InlineData(0.0)]      // no surplus at all — baseline
        [InlineData(400.0)]    // a surplus in every charge quarter
        [InlineData(1500.0)]   // a surplus larger than the remaining headroom
        public void ReachesFullCapacity_RegardlessOfSolarSurplusInBetween(double surplusWh)
        {
            var points = BuildPrices(surplusWh);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 8.0), Options(), FullRangeBounds(points));

            Assert.NotNull(result);

            double peakSoc = result!.Plan.Max(step => step.SocEndKWh);

            // Within one 0.1 kWh arbitrage block of full. The old surplus-reduced ceiling would
            // have stopped this at capacity − surplusWh.
            Assert.True(peakSoc >= CapacityKWh - BlockToleranceKWh,
                $"peak SOC {peakSoc:F3} kWh did not reach capacity {CapacityKWh:F3} kWh " +
                $"with a surplus of {surplusWh:F0} Wh per cheap quarter.");
        }

        /// <summary>One arbitrage block, plus a little room for rounding in the SOC rebuild.</summary>
        private const double BlockToleranceKWh = 0.11;

        [Fact]
        public void ChargeTaper_LowersRequestedPowerAsSocRises()
        {
            var points = BuildPrices(surplusWh: 0.0);
            var bounds = FullRangeBounds(points);

            // ratio = 1.0 at 0% SOC down to 0.3 at 100%, temperature terms neutral.
            var taper = new ChargeTaper(A: 1.0, B: 0.7, C: 0.0, D: 0.0, Samples: 100);

            var tapered = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 8.0, taper), Options(), bounds);
            var untapered = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 8.0), Options(), bounds);

            Assert.NotNull(tapered);
            Assert.NotNull(untapered);

            double taperedPeak = tapered!.Plan.Max(step => step.ChargeKW);
            double untaperedPeak = untapered!.Plan.Max(step => step.ChargeKW);

            Assert.True(taperedPeak < untaperedPeak,
                $"tapered peak charge {taperedPeak:F3} kW should be below the untapered " +
                $"{untaperedPeak:F3} kW.");

            // At 8.0/16.2 = 49% SOC the ratio is 1.0 - 0.7 * 0.49 = 0.65, so the first charge
            // quarter can never ask for the full 7.0 kW.
            Assert.True(taperedPeak <= MaxChargeKW * 0.66,
                $"tapered peak charge {taperedPeak:F3} kW exceeds the taper ceiling.");
        }

        [Fact]
        public void ChargeTaper_StillReachesFullCapacity_GivenEnoughCheapQuarters()
        {
            var points = BuildPrices(surplusWh: 400.0);

            var taper = new ChargeTaper(A: 1.0, B: 0.7, C: 0.0, D: 0.0, Samples: 100);

            var result = BatteryGreedyPlanner.Solve(
                points, Spec(initialSocKWh: 12.0, taper), Options(), FullRangeBounds(points));

            Assert.NotNull(result);

            double peakSoc = result!.Plan.Max(step => step.SocEndKWh);

            Assert.True(peakSoc >= CapacityKWh - BlockToleranceKWh,
                $"peak SOC {peakSoc:F3} kWh did not reach capacity with a taper applied.");
        }
    }
}
