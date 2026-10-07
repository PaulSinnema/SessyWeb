using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Regression for 07-10 18:45 (Candidate F): the baseline assigned all stock to covering the
    /// house later that night at ~€0,34, and no candidate could take that away to sell at €0,396
    /// now. Selling it costs no round trip — the energy is already stored.
    /// </summary>
    public class ShiftDischargeTests
    {
        private static readonly DateTime Start = new(2026, 10, 7, 18, 0, 0);

        // 18:00 sells well, no house load; 18:15..22:00 the house uses 150 Wh/q at a lower price.
        private static List<PricePoint> Points() =>
            Enumerable.Range(0, 17)
                .Select(i => i == 0
                    ? new PricePoint(Start, 0.44, 0.40, NetLoadWh: 0.0, SolarSurplusWh: 0.0)
                    : new PricePoint(Start.AddMinutes(15 * i), 0.34, 0.30, NetLoadWh: 150.0, SolarSurplusWh: 0.0))
                .ToList();

        private static PlanResult Solve()
        {
            var points = Points();
            var bounds = points.Select(p => new SocBound(p.Start, 0.81, 16.2)).ToList();
            // 0.9 × 0.9: rebuying at 0.34 costs 0.42 > 0.40, so Candidate D cannot do this.
            var spec = new BatterySpec(16.2, 3.0, 6.6, 5.1, ChargeEfficiency: 0.9, DischargeEfficiency: 0.9);
            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true);
            return BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;
        }

        [Fact]
        public void Stored_energy_is_sold_now_instead_of_covering_cheaper_house_load_later()
        {
            var plan = Solve();

            // 5.1 kW for a quarter is 1.275 kWh; before Candidate F nothing was sold there.
            Assert.True(plan.Plan[0].DischargeKW * 0.25 > 1.2,
                $"only {plan.Plan[0].DischargeKW * 0.25:F2} kWh sold at €0,40");
            Assert.Equal(ActionMode.Discharge, plan.Plan[0].Mode);
        }

        [Fact]
        public void Quarters_given_up_are_idle_not_zero_net_home()
        {
            var plan = Solve();

            // A ZeroNetHome quarter would cover the house from the battery at runtime anyway.
            // Quarters already at the reserve were never covered and stay ZeroNetHome.
            var idle = plan.Plan.Skip(1).Where(p => p.DischargeKW <= 1e-6 && p.SocStartKWh > 0.82).ToList();
            Assert.NotEmpty(idle);
            // Off where the cover was moved away, SolarOnly once the reserve is reached — never ZNH.
            Assert.All(idle, p => Assert.Contains(p.Mode, new[] { ActionMode.Disabled, ActionMode.SolarOnly }));
        }

        [Fact]
        public void Never_dips_below_the_reserve()
        {
            Assert.All(Solve().Plan, p => Assert.True(p.SocEndKWh >= 0.81 - 0.02, $"{p.Start:HH:mm} SOC {p.SocEndKWh:F2}"));
        }
    }
}
