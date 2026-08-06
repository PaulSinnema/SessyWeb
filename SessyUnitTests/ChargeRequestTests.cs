using SessyCommon.Enums;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// The charge taper is a planning input, not a limit on what the batteries are asked for.
    ///
    /// Until v1.0.37 the setpoint WAS the tapered plan power, while ThrottleAnalysisService
    /// measured the realized power against that same request. As soon as the hardware could meet
    /// the lowered request, the fit was measuring its own request: on production data the requested
    /// share of nameplate fell from 76% (28-07) to 53% (05-08) while the batteries delivered 100%
    /// of every request. These tests pin the three pieces that break that loop.
    /// </summary>
    public class ChargeRequestTests
    {
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 6.6;
        private const double MaxDischargeKW = 5.1;
        private const double Efficiency = 0.95;

        private static readonly DateTime Start = new(2026, 8, 6, 12, 0, 0);

        private static SessyOptions Options() => new(QuarterMinutes: 15, CycleCostEurPerKWh: 0.01);

        private static BatterySpec Spec(ChargeTaper? taper, double initialSocKWh = 1.0) =>
            new(CapacityKWh, initialSocKWh, MaxChargeKW, MaxDischargeKW, Efficiency, Efficiency, taper);

        private static List<SocBound> Bounds(int count) =>
            Enumerable.Range(0, count)
                .Select(i => new SocBound(Start.AddMinutes(15 * i), 0.0, CapacityKWh))
                .ToList();

        /// <summary>Cheap quarters to charge in, then expensive ones to sell into.</summary>
        private static List<PricePoint> Prices(int cheap, int expensive)
        {
            var points = new List<PricePoint>();

            for (int i = 0; i < cheap; i++)
                points.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.10, SellEurPerKWh: 0.10, NetLoadWh: 0.0, SolarSurplusWh: 0.0));

            for (int i = 0; i < expensive; i++)
                points.Add(new PricePoint(Start.AddMinutes(15 * (cheap + i)),
                    BuyEurPerKWh: 0.60, SellEurPerKWh: 0.60, NetLoadWh: 1000.0, SolarSurplusWh: 0.0));

            return points;
        }

        // ── Planner ──────────────────────────────────────────────────────────

        [Fact]
        public void Taper_lowers_expected_charge_but_not_the_request()
        {
            // ratio = 0.5 at every SOC.
            var taper = new ChargeTaper(A: 0.5, B: 0.0, C: 0.0, D: 0.0, Samples: 100);
            var prices = Prices(cheap: 8, expensive: 16);

            var result = BatteryGreedyPlanner.Solve(prices, Spec(taper), Options(), Bounds(prices.Count));

            Assert.NotNull(result);

            var charging = result!.Plan.Where(p => p.Mode == ActionMode.Charge).ToList();
            Assert.NotEmpty(charging);

            var first = charging[0];

            // The plan expects half of nameplate to arrive — that is what the SOC path is built on.
            Assert.Equal(MaxChargeKW * 0.5, first.ChargeKW, 3);

            // ... but the batteries are asked for the full nameplate power. They taper themselves.
            Assert.Equal(MaxChargeKW, first.RequestedChargeKW, 3);
        }

        [Fact]
        public void Without_a_taper_request_equals_planned_power()
        {
            var prices = Prices(cheap: 8, expensive: 16);

            var result = BatteryGreedyPlanner.Solve(prices, Spec(ChargeTaper.None), Options(), Bounds(prices.Count));

            Assert.NotNull(result);

            foreach (var step in result!.Plan.Where(p => p.Mode == ActionMode.Charge))
                Assert.Equal(step.ChargeKW, step.RequestedChargeKW, 3);
        }

        [Fact]
        public void Allocation_that_stops_below_the_cap_is_not_inflated()
        {
            // One expensive quarter can absorb 5.1 * 0.25 = 1.275 kWh, far less than a full
            // quarter of charging, so the greedy allocation stops well below the charge cap.
            // There is no throttling to undo there and the request must equal the allocation.
            var taper = new ChargeTaper(A: 0.5, B: 0.0, C: 0.0, D: 0.0, Samples: 100);
            var prices = Prices(cheap: 4, expensive: 1);

            var result = BatteryGreedyPlanner.Solve(prices, Spec(taper), Options(), Bounds(prices.Count));

            Assert.NotNull(result);

            var charging = result!.Plan.Where(p => p.Mode == ActionMode.Charge).ToList();
            Assert.NotEmpty(charging);

            foreach (var step in charging)
            {
                Assert.True(step.ChargeKW < MaxChargeKW * 0.5 - 1e-6,
                    $"expected an allocation below the taper cap, got {step.ChargeKW:F3} kW");
                Assert.Equal(step.ChargeKW, step.RequestedChargeKW, 3);
            }
        }

        // ── Setpoint ─────────────────────────────────────────────────────────

        [Fact]
        public void Setpoint_is_the_request_not_the_tapered_expectation()
        {
            var act = new MilpServiceBase.PlanAction
            {
                Mode = Modes.Charging,
                PowerW = 3300.0,       // what the taper says will arrive
                RequestedPowerW = 6600.0
            };

            Assert.Equal(6600.0, MilpServiceBase.RequestedChargePowerW(act));
        }

        [Fact]
        public void Setpoint_falls_back_to_plan_power_when_no_request_is_known()
        {
            // Restored plans and runtime overrides carry no request.
            var act = new MilpServiceBase.PlanAction { Mode = Modes.Charging, PowerW = 3300.0 };

            Assert.Equal(3300.0, MilpServiceBase.RequestedChargePowerW(act));
        }

        [Fact]
        public void Setpoint_is_raised_to_the_solar_surplus()
        {
            // 625 Wh of surplus in a quarter = 2500 W. A request of 800 W would have exported the
            // remaining 1700 W at the midday price.
            double setpoint = MilpServiceBase.ChargeSetpointW(requestedW: 800.0, netLoadWh: -625.0, limitWh: 2000.0);

            Assert.Equal(2500.0, setpoint);
        }

        [Fact]
        public void Setpoint_is_capped_by_what_the_session_still_wants()
        {
            // 300 Wh left to charge caps the setpoint at 1200 W, request and surplus regardless.
            double setpoint = MilpServiceBase.ChargeSetpointW(requestedW: 6600.0, netLoadWh: -625.0, limitWh: 300.0);

            Assert.Equal(1200.0, setpoint);
        }

        [Fact]
        public void Setpoint_ignores_a_household_deficit()
        {
            double setpoint = MilpServiceBase.ChargeSetpointW(requestedW: 2000.0, netLoadWh: 400.0, limitWh: 2000.0);

            Assert.Equal(2000.0, setpoint);
        }

        // ── Taper fit ────────────────────────────────────────────────────────

        [Fact]
        public void Envelope_keeps_the_top_of_each_soc_bin()
        {
            // One bin, one sample that reached the hardware limit, twenty that only measured a
            // low request. A mean would land near 0.5; the envelope must stay near the top.
            var samples = new List<(DateTime, double, double, double, double)>();
            var time = Start;

            samples.Add((time, 0.25, 20.0, 20.0, 0.90));
            for (int i = 0; i < 20; i++)
                samples.Add((time.AddMinutes(15 * (i + 1)), 0.25, 20.0, 20.0, 0.50));

            var envelope = ThrottleAnalysisService.SelectEnvelope(samples);

            Assert.Contains(envelope, s => Math.Abs(s.Ratio - 0.90) < 1e-9);
            Assert.True(envelope.Count < samples.Count);
            Assert.True(envelope.Average(s => s.Ratio) > 0.55,
                "the envelope must not be dragged down by samples that only measured the request");
        }

        [Fact]
        public void Envelope_does_not_sink_when_more_low_samples_arrive()
        {
            // The failure mode this replaces: every extra quarter charged at a lowered request
            // pulled the fit down, which lowered the next request again.
            var baseSamples = new List<(DateTime, double, double, double, double)>
            {
                (Start, 0.25, 20.0, 20.0, 0.90),
                (Start.AddMinutes(15), 0.25, 20.0, 20.0, 0.88),
                (Start.AddMinutes(30), 0.25, 20.0, 20.0, 0.86),
            };

            double before = ThrottleAnalysisService.SelectEnvelope(baseSamples).Average(s => s.Ratio);

            var withLowSamples = new List<(DateTime, double, double, double, double)>(baseSamples);
            for (int i = 0; i < 50; i++)
                withLowSamples.Add((Start.AddMinutes(45 + 15 * i), 0.25, 20.0, 20.0, 0.50));

            double after = ThrottleAnalysisService.SelectEnvelope(withLowSamples).Average(s => s.Ratio);

            Assert.True(after >= before - 1e-9,
                $"envelope sank from {before:F3} to {after:F3} on samples that only measured the request");
        }

        [Fact]
        public void Envelope_keeps_the_soc_slope_visible()
        {
            // High ratios at a low SOC, low ratios at a high SOC: the real CC/CV taper. Both bins
            // must survive, otherwise the SOC term has nothing to fit on.
            var samples = new List<(DateTime, double, double, double, double)>();
            var time = Start;

            for (int i = 0; i < 10; i++)
            {
                samples.Add((time.AddMinutes(15 * i), 0.15, 20.0, 20.0, 0.95 - i * 0.01));
                samples.Add((time.AddMinutes(15 * (i + 10)), 0.85, 20.0, 20.0, 0.60 - i * 0.01));
            }

            var envelope = ThrottleAnalysisService.SelectEnvelope(samples);

            Assert.Contains(envelope, s => s.Soc < 0.5);
            Assert.Contains(envelope, s => s.Soc > 0.5);
            Assert.True(envelope.Where(s => s.Soc < 0.5).Average(s => s.Ratio) >
                        envelope.Where(s => s.Soc > 0.5).Average(s => s.Ratio));
        }
    }
}
