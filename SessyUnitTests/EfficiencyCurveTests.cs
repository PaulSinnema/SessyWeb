using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Efficiency depends on power, and the planner has to see it.
    ///
    /// A large part of the conversion loss is fixed overhead — the electronics draw their tens of
    /// watts whether they move 500 W or 5000 W — so the same energy survives better in fewer,
    /// fuller quarters. Measured on the production database: 0.80 below 1 kW against 0.92 at
    /// 4-5 kW. With a constant efficiency the planner sees spreading as free and has no reason to
    /// concentrate, which is what left the short cheap windows underfilled.
    /// </summary>
    public class EfficiencyCurveTests
    {
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 6.6;
        private const double MaxDischargeKW = 5.1;

        private static readonly DateTime Start = new(2026, 8, 8, 12, 0, 0);

        // ── The curve itself ──────────────────────────────────────────────────

        [Fact]
        public void Efficiency_rises_with_power()
        {
            var curve = new EfficiencyCurve(0.94, 0.07, 0.94, 0.07, Samples: 100);

            Assert.True(curve.ChargeAt(5.0) > curve.ChargeAt(1.0));
            Assert.Equal(0.87, curve.ChargeAt(1.0), 2);      // 0.94 - 0.07/1
            Assert.Equal(0.926, curve.ChargeAt(5.0), 3);     // 0.94 - 0.07/5
        }

        [Fact]
        public void A_flat_curve_ignores_power()
        {
            var flat = EfficiencyCurve.Flat(0.95, 0.93);

            Assert.Equal(0.95, flat.ChargeAt(0.5));
            Assert.Equal(0.95, flat.ChargeAt(6.0));
            Assert.Equal(0.93, flat.DischargeAt(0.5));
        }

        [Fact]
        public void The_curve_never_leaves_plausible_bounds()
        {
            // A steep overhead would send a near-idle battery to a negative efficiency.
            var curve = new EfficiencyCurve(0.94, 0.7, 0.94, 0.7, Samples: 100);

            Assert.InRange(curve.ChargeAt(0.01), 0.50, 0.99);
            Assert.InRange(curve.ChargeAt(100.0), 0.50, 0.99);
        }

        // ── Fitting it from measurements ──────────────────────────────────────

        private static QuarterlyMeasurement Measurement(DateTime time, double powerW, double socWh) =>
            new() { Time = time, BatteryPowerWatts = powerW, BatteryStateOfChargeWh = socWh, IsReliable = true };

        [Fact]
        public void Fit_recovers_a_fixed_overhead_from_charge_samples()
        {
            // Build quarters at alternating powers whose stored energy follows
            // eff = 0.95 - 0.05/kW exactly.
            var measurements = new List<QuarterlyMeasurement>();
            var time = Start;
            double soc = 2000.0;

            measurements.Add(Measurement(time, 0.0, soc));

            for (int i = 0; i < 60; i++)
            {
                double powerKW = 1.0 + (i % 5);                 // 1..5 kW
                double efficiency = 0.95 - 0.05 / powerKW;
                double stored = powerKW * 1000.0 * 0.25 * efficiency;

                soc += stored;
                time = time.AddMinutes(15);
                measurements.Add(Measurement(time, -powerKW * 1000.0, soc));
            }

            var curve = BatteryEfficiencyService.Fit(measurements, EfficiencyCurve.Flat(0.9, 0.9));

            Assert.Equal(0.95, curve.ChargeCeiling, 2);
            Assert.Equal(0.05, curve.ChargeOverheadKW, 2);
        }

        [Fact]
        public void Fit_falls_back_to_flat_without_enough_samples()
        {
            var measurements = new List<QuarterlyMeasurement>
            {
                Measurement(Start, 0.0, 2000.0),
                Measurement(Start.AddMinutes(15), -2000.0, 2450.0),
            };

            var curve = BatteryEfficiencyService.Fit(measurements, EfficiencyCurve.Flat(0.91, 0.89));

            Assert.Equal(0.91, curve.ChargeCeiling);
            Assert.Equal(0.0, curve.ChargeOverheadKW);
            Assert.Equal(0.91, curve.ChargeAt(1.0));       // still flat
        }

        [Fact]
        public void Fit_skips_gaps_between_quarters()
        {
            // Two quarters an hour apart: the SOC difference covers three unmeasured quarters and
            // would report a wildly wrong efficiency.
            var measurements = new List<QuarterlyMeasurement>
            {
                Measurement(Start, -2000.0, 2000.0),
                Measurement(Start.AddHours(1), -2000.0, 4000.0),
            };

            var curve = BatteryEfficiencyService.Fit(measurements, EfficiencyCurve.Flat(0.9, 0.9));

            Assert.Equal(0, curve.Samples);
        }

        // ── What it does to the plan ──────────────────────────────────────────

        private static List<PricePoint> FlatCheapWindow(int cheapQuarters, int expensiveQuarters)
        {
            var points = new List<PricePoint>();

            // Identical cheap prices, so nothing but efficiency can decide how to spread the load.
            for (int i = 0; i < cheapQuarters; i++)
                points.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.10, SellEurPerKWh: 0.10, NetLoadWh: 0.0, SolarSurplusWh: 0.0));

            for (int i = 0; i < expensiveQuarters; i++)
                points.Add(new PricePoint(Start.AddMinutes(15 * (cheapQuarters + i)),
                    BuyEurPerKWh: 0.60, SellEurPerKWh: 0.60, NetLoadWh: 0.0, SolarSurplusWh: 0.0));

            return points;
        }

        /// <summary>
        /// A choice between a cheap quarter that can only take a trickle and a slightly dearer one
        /// that can take real power. The trickle is 0,5 ct cheaper per kWh, so on price alone it
        /// wins; at 1 kW it loses so much more of the energy in conversion that it should not.
        /// </summary>
        private static PlanResult SolveTrickleVersusBulk(EfficiencyCurve curve)
        {
            var points = new List<PricePoint>
            {
                // Cheapest, but throttled to 1 kW.
                new(Start, BuyEurPerKWh: 0.100, SellEurPerKWh: 0.100, NetLoadWh: 0.0,
                    SolarSurplusWh: 0.0, MaxChargeKW: 1.0),

                // A shade dearer, but takes 5 kW.
                new(Start.AddMinutes(15), BuyEurPerKWh: 0.105, SellEurPerKWh: 0.105, NetLoadWh: 0.0,
                    SolarSurplusWh: 0.0, MaxChargeKW: 5.0),
            };

            // One expensive quarter to sell into; deliberately small, so the planner can only place
            // one block and has to choose which charge quarter feeds it.
            points.Add(new PricePoint(Start.AddMinutes(30),
                BuyEurPerKWh: 0.60, SellEurPerKWh: 0.60, NetLoadWh: 0.0, SolarSurplusWh: 0.0,
                MaxDischargeKW: 0.4));

            var spec = new BatterySpec(
                CapacityKWh, InitialSocKWh: 0.0, MaxChargeKW, MaxDischargeKW,
                ChargeEfficiency: 0.95, DischargeEfficiency: 0.95,
                ChargeTaper: null, Efficiency: curve);

            var bounds = points
                .Select(p => new SocBound(p.Start, 0.0, CapacityKWh))
                .ToList();

            var result = BatteryGreedyPlanner.Solve(
                points, spec, new SessyOptions(15, CycleCostEurPerKWh: 0.01), bounds);

            Assert.NotNull(result);
            return result!;
        }

        [Fact]
        public void Without_the_curve_the_cheapest_quarter_wins_however_slow()
        {
            var plan = SolveTrickleVersusBulk(EfficiencyCurve.Flat(0.95, 0.95));

            // Flat efficiency sees no downside to the trickle, so price decides.
            Assert.True(plan.Plan[0].ChargeKW > plan.Plan[1].ChargeKW,
                "with a flat curve the cheapest quarter should win regardless of its power");
        }

        [Fact]
        public void With_the_curve_the_quarter_that_takes_real_power_wins()
        {
            var plan = SolveTrickleVersusBulk(new EfficiencyCurve(0.98, 0.10, 0.98, 0.10, Samples: 100));

            // 0.98 - 0.10/1 = 0.88 against 0.98 - 0.10/5 = 0.96. Half a cent of price does not
            // cover eight points of efficiency, so the dearer, faster quarter is the better buy.
            Assert.True(plan.Plan[1].ChargeKW > plan.Plan[0].ChargeKW,
                $"expected the 5 kW quarter to be preferred, got {plan.Plan[0].ChargeKW:F3} kW at 0.100 " +
                $"against {plan.Plan[1].ChargeKW:F3} kW at 0.105");
        }

        /// <summary>
        /// Documents where the spreading in production actually comes from. With equal prices the
        /// greedy allocation already fills one quarter to its cap before opening the next, curve or
        /// no curve — so the efficiency curve is not what concentrates a flat cheap window. What
        /// spreads the load there is the per-quarter cap (the charge taper), and that is a separate
        /// decision, deliberately left alone until the taper fit is trustworthy.
        /// </summary>
        [Fact]
        public void On_equal_prices_the_planner_already_fills_quarter_by_quarter()
        {
            var prices = FlatCheapWindow(cheapQuarters: 16, expensiveQuarters: 16);

            var bounds = Enumerable.Range(0, prices.Count)
                .Select(i => new SocBound(Start.AddMinutes(15 * i), 0.0, CapacityKWh))
                .ToList();

            var options = new SessyOptions(15, CycleCostEurPerKWh: 0.01);

            PlanResult Solve(EfficiencyCurve curve) =>
                BatteryGreedyPlanner.Solve(prices,
                    new BatterySpec(CapacityKWh, 0.0, MaxChargeKW, MaxDischargeKW, 0.95, 0.95,
                        ChargeTaper: null, Efficiency: curve),
                    options, bounds)!;

            var flat = Solve(EfficiencyCurve.Flat(0.95, 0.95));
            var curved = Solve(new EfficiencyCurve(0.98, 0.10, 0.98, 0.10, Samples: 100));

            int flatQuarters = flat.Plan.Count(p => p.ChargeKW > 0.01);
            int curvedQuarters = curved.Plan.Count(p => p.ChargeKW > 0.01);

            Assert.Equal(flatQuarters, curvedQuarters);

            // And they are full ones, not a thin layer spread over the window.
            Assert.True(flat.Plan.Where(p => p.ChargeKW > 0.01).Average(p => p.ChargeKW) > MaxChargeKW * 0.8);
        }

        [Fact]
        public void Without_a_curve_the_plan_is_unchanged()
        {
            // The null curve is the compatibility guarantee: same numbers as before this existed.
            var prices = FlatCheapWindow(8, 8);
            var bounds = Enumerable.Range(0, prices.Count)
                .Select(i => new SocBound(Start.AddMinutes(15 * i), 0.0, CapacityKWh))
                .ToList();

            var options = new SessyOptions(15, CycleCostEurPerKWh: 0.01);

            var withNull = BatteryGreedyPlanner.Solve(prices,
                new BatterySpec(CapacityKWh, 0.0, MaxChargeKW, MaxDischargeKW, 0.95, 0.95),
                options, bounds);

            var withFlat = BatteryGreedyPlanner.Solve(prices,
                new BatterySpec(CapacityKWh, 0.0, MaxChargeKW, MaxDischargeKW, 0.95, 0.95,
                    ChargeTaper: null, Efficiency: EfficiencyCurve.Flat(0.95, 0.95)),
                options, bounds);

            Assert.NotNull(withNull);
            Assert.NotNull(withFlat);
            Assert.Equal(withNull!.ObjectiveEur, withFlat!.ObjectiveEur, 6);

            for (int i = 0; i < withNull.Plan.Count; i++)
            {
                Assert.Equal(withNull.Plan[i].ChargeKW, withFlat.Plan[i].ChargeKW, 6);
                Assert.Equal(withNull.Plan[i].DischargeKW, withFlat.Plan[i].DischargeKW, 6);
            }
        }
    }
}
