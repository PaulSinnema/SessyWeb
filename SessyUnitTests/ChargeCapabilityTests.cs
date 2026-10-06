using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Sustained charge capability from SOC gain (v1.0.146). On 15-09..06-10 the plan asked
    /// 4.7-5.4 kW above 40% SOC where the bank sustained 3.2-3.9 kW: taper and floor keep the top
    /// of momentary snapshots. The median SOC gain per bin is what the plan should expect.
    /// </summary>
    public class ChargeCapabilityTests
    {
        private static List<(double Soc, double PowerW)> Bin(double soc, params double[] powers)
            => powers.Select(p => (soc, p)).ToList();

        [Fact]
        public void No_samples_means_no_capability()
        {
            var capability = ThrottleAnalysisService.FitChargeCapability([]);

            Assert.Equal(0, capability.Samples);
            Assert.Equal(0.0, capability.PowerW(0.5));
        }

        [Fact]
        public void A_thin_bin_stays_empty()
        {
            var capability = ThrottleAnalysisService.FitChargeCapability(Bin(0.55, 3000, 3100, 3200));

            Assert.Equal(0.0, capability.PowerW(0.55));
        }

        [Fact]
        public void The_bin_is_the_median_not_the_top()
        {
            // One momentary 6.5 kW sample among ~3.3 kW must not move the bin.
            var capability = ThrottleAnalysisService.FitChargeCapability(
                Bin(0.65, 3100, 3200, 3250, 3300, 3300, 3350, 3400, 3450, 6500));

            Assert.Equal(3300.0, capability.PowerW(0.65), 3);
            Assert.Equal(1, capability.CoveredBins);
        }

        // ── What it does to a plan ────────────────────────────────────────────

        private const int Cheap = 40;

        // One cheap quarter, dear selling after it: what lands there is the cap the planner believes.
        private static List<PricePoint> OneCheapQuarter() =>
            Enumerable.Range(0, 96)
                .Select(i => new PricePoint(
                    Start: new DateTime(2026, 10, 6, 0, 0, 0).AddMinutes(15 * i),
                    BuyEurPerKWh: i == Cheap ? 0.05 : 0.40,
                    SellEurPerKWh: i > Cheap ? 0.38 : 0.02,
                    NetLoadWh: 0,
                    SolarSurplusWh: 0))
                .ToList();

        private static BatterySpec Spec(ChargeCapability? capability) =>
            new(CapacityKWh: 16.2, InitialSocKWh: 11.0, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.92, DischargeEfficiency: 0.92,
                ChargeTaper: null, Efficiency: null, DischargeCapability: null,
                // Snapshot floor claiming 5.3 kW everywhere — the production shape.
                ChargeFloor: new ChargeCapabilityFloor(Enumerable.Repeat(5300.0, 20).ToArray(), 500),
                ChargeCapability: capability);

        private static readonly SessyOptions Options = new(15, CycleCostEurPerKWh: 0.05, AllowExport: true);

        private static double ChargeAtCheapKW(ChargeCapability? capability)
        {
            var points = OneCheapQuarter();
            var bounds = points.Select(p => new SocBound(p.Start, 0.0, 16.2)).ToList();
            return BatteryGreedyPlanner.Solve(points, Spec(capability), Options, bounds)!.Plan[Cheap].ChargeKW;
        }

        [Fact]
        public void Measured_capability_replaces_the_floor_where_it_has_data()
        {
            // 68% SOC: bin 6 measured at 3.3 kW DC.
            var bins = new double[10];
            bins[6] = 3300.0;
            var capability = new ChargeCapability(bins, 100);

            double withFloor = ChargeAtCheapKW(null);
            double withCapability = ChargeAtCheapKW(capability);

            Assert.True(withFloor > 5.0, $"floor alone should allow ~5.3 kW, got {withFloor:F2}");
            // 3.3 kW DC at 0.92 efficiency is ~3.6 kW AC.
            Assert.InRange(withCapability, 3.4, 3.8);
        }

        [Fact]
        public void An_empty_bin_falls_back_to_taper_and_floor()
        {
            // Only bin 2 measured; the cheap quarter starts at 68% SOC.
            var bins = new double[10];
            bins[2] = 5000.0;

            Assert.Equal(ChargeAtCheapKW(null), ChargeAtCheapKW(new ChargeCapability(bins, 100)), 6);
        }
    }
}
