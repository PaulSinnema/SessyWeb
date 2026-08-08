using SessyCommon.Enums;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Deliverable discharge power depends on state of charge, not on outside temperature.
    ///
    /// Measured on the production database temperature explains R2 = 0.012 of the discharge
    /// throttle (t = 0.93, insignificant) while SOC explains 0.225 (t = +4.43), and the envelope
    /// of achieved power is flat at ~4100 W from roughly 20% SOC upwards and collapses below it.
    /// Battery power itself was tested as a regressor — prior power over 1, 2 and 4 hours, energy
    /// moved so far in the session, quarters into the session — and none of them reached
    /// significance, with the sign pointing the wrong way for a heat effect.
    ///
    /// The old model got two further things wrong that these tests pin: it averaged the samples
    /// where it should take their upper envelope, and it commanded the already-capped power while
    /// reconstructing the target as plan / ratio, which made every ratio reproduce itself.
    /// </summary>
    public class DischargeCapabilityTests
    {
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 6.6;
        private const double MaxDischargeKW = 5.1;
        private const double Efficiency = 0.95;
        private const double NameplateW = 5100.0;

        private static readonly DateTime Start = new(2026, 8, 8, 18, 0, 0);

        private static SessyOptions Options() => new(QuarterMinutes: 15, CycleCostEurPerKWh: 0.01);

        private static BatterySpec Spec(DischargeCapability? capability, double initialSocKWh) =>
            new(CapacityKWh, initialSocKWh, MaxChargeKW, MaxDischargeKW, Efficiency, Efficiency,
                ChargeTaper: null, Efficiency: null, DischargeCapability: capability);

        private static List<SocBound> Bounds(int count) =>
            Enumerable.Range(0, count)
                .Select(i => new SocBound(Start.AddMinutes(15 * i), 0.0, CapacityKWh))
                .ToList();

        /// <summary>Plateau above the knee, proportional below it — the shape being fitted.</summary>
        private static double TrueModel(double soc, double plateauW, double kneeSoc)
            => soc >= kneeSoc ? plateauW : plateauW * soc / kneeSoc;

        /// <summary>
        /// Three samples in every 5% SOC bin, the best of which sits exactly on the model. The
        /// other two are well below it, as real quarters are whenever the plan asked for less.
        /// </summary>
        private static List<(double Soc, double PowerW)> Samples(double plateauW, double kneeSoc)
        {
            var samples = new List<(double, double)>();

            for (int bin = 0; bin < 20; bin++)
            {
                double soc = (bin + 0.5) / 20.0;
                double best = TrueModel(soc, plateauW, kneeSoc);

                samples.Add((soc, best));
                samples.Add((soc, best * 0.55));
                samples.Add((soc, best * 0.30));
            }

            return samples;
        }

        // ── Fit ──────────────────────────────────────────────────────────────

        [Fact]
        public void Fit_recovers_the_plateau_and_the_knee()
        {
            var fit = ThrottleAnalysisService.FitDischargeCapability(
                Samples(plateauW: 4000.0, kneeSoc: 0.20), NameplateW);

            Assert.Equal(4000.0, fit.PlateauW, 1);
            Assert.Equal(0.20, fit.KneeSoc, 3);
            Assert.Equal(20, fit.Samples);
        }

        [Fact]
        public void Fit_takes_the_best_of_a_bin_not_its_average()
        {
            // Same evidence, buried under twenty quarters that were never asked for full power.
            // A mean would land near 900 W; the envelope must still see 4000.
            var samples = Samples(plateauW: 4000.0, kneeSoc: 0.20);

            for (int bin = 0; bin < 20; bin++)
                for (int i = 0; i < 20; i++)
                    samples.Add(((bin + 0.5) / 20.0, 600.0));

            var fit = ThrottleAnalysisService.FitDischargeCapability(samples, NameplateW);

            Assert.Equal(4000.0, fit.PlateauW, 1);
            Assert.Equal(0.20, fit.KneeSoc, 3);
        }

        [Fact]
        public void Too_few_bins_yield_no_fit_so_the_caller_keeps_its_own_limit()
        {
            var samples = new List<(double Soc, double PowerW)>();

            // Three bins is not a curve, however many samples sit in them.
            foreach (double soc in new[] { 0.42, 0.47, 0.52 })
                for (int i = 0; i < 50; i++)
                    samples.Add((soc, 4000.0));

            var fit = ThrottleAnalysisService.FitDischargeCapability(samples, NameplateW);

            Assert.Equal(0, fit.Samples);
            Assert.Same(DischargeCapability.None, fit);
        }

        [Fact]
        public void Fit_never_reports_more_than_nameplate()
        {
            // A plateau above nameplate is a measurement error, not a discovery.
            var fit = ThrottleAnalysisService.FitDischargeCapability(
                Samples(plateauW: 6000.0, kneeSoc: 0.20), NameplateW);

            Assert.Equal(NameplateW, fit.PlateauW, 1);
        }

        [Fact]
        public void Capability_is_flat_above_the_knee_and_proportional_below_it()
        {
            var capability = new DischargeCapability(PlateauW: 4000.0, KneeSoc: 0.20, Samples: 20);

            Assert.Equal(4000.0, capability.PowerW(0.20), 1);
            Assert.Equal(4000.0, capability.PowerW(0.90), 1);
            Assert.Equal(2000.0, capability.PowerW(0.10), 1);
            Assert.Equal(0.0, capability.PowerW(0.0), 1);
        }

        // ── Planner ──────────────────────────────────────────────────────────

        /// <summary>Nothing to charge from, plenty to sell into: the plan discharges flat out.</summary>
        private static List<PricePoint> ExportPrices(int count)
            => Enumerable.Range(0, count)
                .Select(i => new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.60, SellEurPerKWh: 0.60, NetLoadWh: 0.0, SolarSurplusWh: 0.0))
                .ToList();

        [Fact]
        public void Plan_is_capped_at_the_plateau_not_at_nameplate()
        {
            var capability = new DischargeCapability(PlateauW: 4000.0, KneeSoc: 0.20, Samples: 20);
            var prices = ExportPrices(4);

            var result = BatteryGreedyPlanner.Solve(
                prices, Spec(capability, initialSocKWh: 10.0), Options(), Bounds(prices.Count));

            Assert.NotNull(result);

            var discharging = result!.Plan.Where(p => p.Mode == ActionMode.Discharge).ToList();
            Assert.NotEmpty(discharging);
            Assert.Equal(4.0, discharging[0].DischargeKW, 2);
        }

        [Fact]
        public void Below_the_knee_the_plan_is_capped_proportionally()
        {
            var capability = new DischargeCapability(PlateauW: 4000.0, KneeSoc: 0.20, Samples: 20);
            var prices = ExportPrices(4);

            // 1.62 kWh of 16.2 is 10% SOC — half the knee, so half the plateau.
            var result = BatteryGreedyPlanner.Solve(
                prices, Spec(capability, initialSocKWh: 1.62), Options(), Bounds(prices.Count));

            Assert.NotNull(result);

            var first = result!.Plan.First(p => p.Mode == ActionMode.Discharge);
            Assert.Equal(2.0, first.DischargeKW, 2);
        }

        [Fact]
        public void Request_stays_at_nameplate_where_the_capability_was_the_binding_cap()
        {
            var capability = new DischargeCapability(PlateauW: 4000.0, KneeSoc: 0.20, Samples: 20);
            var prices = ExportPrices(4);

            var result = BatteryGreedyPlanner.Solve(
                prices, Spec(capability, initialSocKWh: 10.0), Options(), Bounds(prices.Count));

            Assert.NotNull(result);

            var first = result!.Plan.First(p => p.Mode == ActionMode.Discharge);

            // The batteries derate themselves; asking for the derated number only guarantees we
            // stay under their limit, and it is what froze the measured throttle.
            Assert.Equal(4.0, first.DischargeKW, 2);
            Assert.Equal(MaxDischargeKW, first.RequestedDischargeKW, 2);
        }

        [Fact]
        public void Without_a_capability_the_plan_and_the_request_coincide()
        {
            var prices = ExportPrices(4);

            var result = BatteryGreedyPlanner.Solve(
                prices, Spec(capability: null, initialSocKWh: 10.0), Options(), Bounds(prices.Count));

            Assert.NotNull(result);

            var first = result!.Plan.First(p => p.Mode == ActionMode.Discharge);
            Assert.Equal(MaxDischargeKW, first.DischargeKW, 2);
            Assert.Equal(MaxDischargeKW, first.RequestedDischargeKW, 2);
        }

        // ── Execution ────────────────────────────────────────────────────────

        [Fact]
        public void Setpoint_is_the_request_not_the_capped_expectation()
        {
            var act = new MilpServiceBase.PlanAction
            {
                Mode = Modes.Discharging,
                PowerW = 4000.0,           // what the capability says will come out
                RequestedPowerW = 5100.0
            };

            Assert.Equal(5100.0, MilpServiceBase.RequestedDischargePowerW(act));
        }

        [Fact]
        public void Setpoint_falls_back_to_plan_power_when_no_request_is_known()
        {
            // A restored plan or a runtime override carries no request.
            var act = new MilpServiceBase.PlanAction
            {
                Mode = Modes.Discharging,
                PowerW = 4000.0,
                RequestedPowerW = 0.0
            };

            Assert.Equal(4000.0, MilpServiceBase.RequestedDischargePowerW(act));
        }
    }
}
