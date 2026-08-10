using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// The floor under the charge taper.
    ///
    /// It exists because the taper is fitted on realized/requested and can only use quarters that
    /// recorded an untapered request — 223 of 7135 on the production database, all from one
    /// heatwave. Fitted on that it predicted 2.3 kW at 80% SOC, the planner believed it, and the
    /// evening of 11-08-2026 sold 6.6 kWh instead of 14.2. Watts need no denominator, and a high
    /// measurement proves capability where a low one proves nothing — hence a floor, not a fit.
    /// </summary>
    public class ChargeCapabilityFloorTests
    {
        private const double NameplateW = 6600.0;

        private static List<(double Soc, double PowerW)> Bin(double soc, params double[] powers)
            => powers.Select(p => (soc, p)).ToList();

        [Fact]
        public void No_samples_means_no_floor()
        {
            Assert.Equal(0, ChargeCapabilityFloor.None.Samples);
            Assert.Equal(0.0, ChargeCapabilityFloor.None.PowerW(0.5));

            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor([], NameplateW);

            Assert.Equal(0, floor.Samples);
            Assert.Equal(0.0, floor.PowerW(0.5));
        }

        [Fact]
        public void A_bin_with_too_few_samples_carries_no_floor()
        {
            // Two samples in the 50-55% bin: not enough to claim anything about that region.
            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(Bin(0.52, 5000, 5100), NameplateW);

            Assert.Equal(0.0, floor.PowerW(0.52));
        }

        [Fact]
        public void The_floor_is_the_top_of_the_bin_with_a_margin()
        {
            // Ten samples, P90 with interpolation lands on 5090; the margin takes 10% off.
            var powers = new double[] { 4100, 4650, 4900, 5050, 5100, 5180, 5240, 5290, 5318, 5335 };

            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(Bin(0.72, powers), NameplateW);

            double expected = Percentiles.Percentile([.. powers.OrderBy(p => p)], 90.0) * 0.9;

            Assert.Equal(expected, floor.PowerW(0.72), 3);

            // Sits in the top of the cloud, and the margin keeps it under the very best sample.
            Assert.True(floor.PowerW(0.72) > powers.Min());
            Assert.True(floor.PowerW(0.72) < powers.Max());
        }

        [Fact]
        public void One_freak_reading_cannot_set_the_floor_on_its_own()
        {
            var honest = new double[] { 2000, 2050, 2100, 2150, 2200, 2250, 2300, 2350, 2400, 2450 };
            var withOutlier = honest.Append(6500.0).ToArray();

            var clean = ThrottleAnalysisService.FitChargeCapabilityFloor(Bin(0.72, honest), NameplateW);
            var dirty = ThrottleAnalysisService.FitChargeCapabilityFloor(Bin(0.72, withOutlier), NameplateW);

            // A maximum would have jumped to 5850 W; the percentile barely moves.
            Assert.True(dirty.PowerW(0.72) < clean.PowerW(0.72) * 1.3,
                $"outlier moved the floor from {clean.PowerW(0.72):F0} to {dirty.PowerW(0.72):F0} W");
        }

        [Fact]
        public void The_floor_never_exceeds_nameplate()
        {
            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(
                Bin(0.30, 20000, 21000, 22000, 23000), NameplateW);

            Assert.Equal(NameplateW, floor.PowerW(0.30));
        }

        [Fact]
        public void Each_bin_stands_on_its_own()
        {
            var samples = Bin(0.25, 5000, 5100, 5200)
                .Concat(Bin(0.85, 2000, 2100, 2200))
                .ToList();

            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(samples, NameplateW);

            Assert.True(floor.PowerW(0.25) > 4000.0);
            Assert.True(floor.PowerW(0.85) < 2500.0);
            Assert.Equal(0.0, floor.PowerW(0.55));   // never measured there
            Assert.Equal(2, floor.CoveredBins);
        }

        // ── What it does to a plan ────────────────────────────────────────────

        private static List<PricePoint> FlatDay(int quarters) =>
            Enumerable.Range(0, quarters)
                .Select(i => new PricePoint(
                    Start: new DateTime(2026, 8, 11, 0, 0, 0).AddMinutes(15 * i),
                    BuyEurPerKWh: i < quarters / 2 ? 0.10 : 0.40,
                    SellEurPerKWh: i < quarters / 2 ? 0.08 : 0.38,
                    NetLoadWh: 100,
                    SolarSurplusWh: 0))
                .ToList();

        private static BatterySpec SpecWith(ChargeTaper? taper, ChargeCapabilityFloor? floor) =>
            new(CapacityKWh: 16.2, InitialSocKWh: 2.0, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.92, DischargeEfficiency: 0.92,
                ChargeTaper: taper, Efficiency: null, DischargeCapability: null, ChargeFloor: floor);

        private static readonly SessyOptions Options =
            new(15, CycleCostEurPerKWh: 0.05, AllowExport: true);

        [Fact]
        public void Without_a_floor_the_plan_is_exactly_what_it_was()
        {
            var points = FlatDay(96);
            var bounds = points.Select(p => new SocBound(p.Start, 0.0, 16.2)).ToList();

            var taper = new ChargeTaper(0.95, 0.75, 0.0, 0.0, 100);

            var before = BatteryGreedyPlanner.Solve(points, SpecWith(taper, null), Options, bounds);
            var after = BatteryGreedyPlanner.Solve(points, SpecWith(taper, ChargeCapabilityFloor.None), Options, bounds);

            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before!.ObjectiveEur, after!.ObjectiveEur, 9);

            for (int i = 0; i < before.Plan.Count; i++)
                Assert.Equal(before.Plan[i].ChargeKW, after.Plan[i].ChargeKW, 9);
        }

        [Fact]
        public void A_floor_above_the_taper_lets_the_plan_charge_faster()
        {
            // One cheap quarter, everything else dear: the plan wants to put as much as it can into
            // that single quarter, so what lands there IS the cap the planner believes in.
            const int cheap = 40;

            var points = Enumerable.Range(0, 96)
                .Select(i => new PricePoint(
                    Start: new DateTime(2026, 8, 11, 0, 0, 0).AddMinutes(15 * i),
                    BuyEurPerKWh: i == cheap ? 0.05 : 0.40,
                    // Selling only pays after the cheap quarter, so the battery still holds its
                    // charge when it gets there — otherwise it has sold down, the taper allows
                    // plenty at a low state of charge, and the test measures nothing.
                    SellEurPerKWh: i > cheap ? 0.38 : 0.02,
                    // No household load either: it would quietly drain the battery on the way to
                    // the cheap quarter, and the taper would then be evaluated at a much lower
                    // state of charge than the one under test.
                    NetLoadWh: 0,
                    SolarSurplusWh: 0))
                .ToList();

            var bounds = points.Select(p => new SocBound(p.Start, 0.0, 16.2)).ToList();

            // 80% full, where the taper allows about 2.3 kW of the 6.6 kW nameplate while the
            // measurements say 5 kW — the exact shape of the production case.
            var spec = SpecWith(new ChargeTaper(0.95, 0.75, 0.0, 0.0, 100), null) with { InitialSocKWh = 12.96 };
            var floor = new ChargeCapabilityFloor(Enumerable.Repeat(5000.0, 20).ToArray(), 500);

            var withTaper = BatteryGreedyPlanner.Solve(points, spec, Options, bounds);
            var withFloor = BatteryGreedyPlanner.Solve(points, spec with { ChargeFloor = floor }, Options, bounds);

            Assert.NotNull(withTaper);
            Assert.NotNull(withFloor);

            double taperKW = withTaper!.Plan[cheap].ChargeKW;
            double floorKW = withFloor!.Plan[cheap].ChargeKW;

            Assert.True(taperKW < 3.0, $"taper should hold this quarter near 2.3 kW, got {taperKW:F2}");
            Assert.True(floorKW > taperKW + 1.0,
                $"floor charged {floorKW:F2} kW where the taper allowed {taperKW:F2} kW");
        }

        [Fact]
        public void The_floor_never_lifts_a_quarter_past_its_own_power_limit()
        {
            var points = FlatDay(96)
                .Select(p => p with { MaxChargeKW = 2.0 })
                .ToList();

            var bounds = points.Select(p => new SocBound(p.Start, 0.0, 16.2)).ToList();
            var floor = new ChargeCapabilityFloor(Enumerable.Repeat(5000.0, 20).ToArray(), 500);

            var plan = BatteryGreedyPlanner.Solve(points, SpecWith(null, floor), Options, bounds);

            Assert.NotNull(plan);
            Assert.All(plan!.Plan, step => Assert.True(step.ChargeKW <= 2.0 + 1e-6));
        }
    }
}
