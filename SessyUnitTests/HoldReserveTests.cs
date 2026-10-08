using SessyCommon.Enums;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// HoldReserve (v1.0.148): the night of 06-10 the plan held the reserve but labelled those
    /// quarters Zero Net Home, which covers the whole house at runtime — the batteries ran to 0%.
    /// Once the reserve is reached the plan says HoldReserve: store live surplus, never discharge.
    /// </summary>
    public class HoldReserveTests
    {
        private const double ReserveKWh = 0.81;
        private static readonly DateTime Start = new(2026, 10, 6, 21, 0, 0);

        // ── Execution: the P1 target ──────────────────────────────────────────

        [Theory]
        [InlineData(-500.0, 0)]     // surplus > 0: target 0, battery stores 500 W
        [InlineData(-1.0, 0)]       // smallest surplus still stores
        [InlineData(0.0, 0)]        // surplus 0: target = house net (0), battery idle
        [InlineData(700.0, 700)]    // deficit: target = house net, battery delivers nothing
        public void Target_stores_surplus_and_never_covers_a_deficit(double houseNetW, int expectedTargetW)
        {
            Assert.Equal(expectedTargetW, GridTargetCalculator.HoldReserveTargetW(houseNetW));
            Assert.Equal(expectedTargetW, GridTargetCalculator.GridTargetW(Modes.HoldReserve, houseNetW, 0, 6600, 5100));

            // Implied battery power = house net - target: never positive (= never discharging).
            Assert.True(houseNetW - expectedTargetW <= 0.0);
        }

        // ── Planning ──────────────────────────────────────────────────────────

        private static PlanResult Solve(double initialSocKWh, Func<int, double> netLoadWh)
        {
            var points = Enumerable.Range(0, 32)
                .Select(i => new PricePoint(Start.AddMinutes(15 * i), 0.30, 0.26, NetLoadWh: netLoadWh(i),
                    SolarSurplusWh: Math.Max(0.0, -netLoadWh(i))))
                .ToList();
            var bounds = points.Select(p => new SocBound(p.Start, ReserveKWh, 16.2)).ToList();
            var spec = new BatterySpec(16.2, initialSocKWh, 6.6, 5.1, 0.9, 0.9);
            var opt = new SessyOptions(15, CycleCostEurPerKWh: 0.001, AllowExport: true);
            return BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;
        }

        [Fact]
        public void House_is_covered_until_the_reserve_then_HoldReserve()
        {
            // 180 Wh per quarter of house load from 1.5 kWh: a few quarters fit above 0.81.
            var plan = Solve(1.5, _ => 180.0);

            var znh = plan.Plan.Where(p => p.Mode == ActionMode.ZeroNetHome).ToList();
            Assert.NotEmpty(znh);
            Assert.All(znh, p => Assert.True(p.SocEndKWh >= ReserveKWh - 0.01, $"{p.Start:HH:mm} ZNH ends at {p.SocEndKWh:F2}"));

            // Once the next full quarter no longer fits, every quarter is HoldReserve with no discharge.
            int first = plan.Plan.ToList().FindIndex(p => p.Mode == ActionMode.HoldReserve);
            Assert.True(first > 0);
            Assert.All(plan.Plan.Skip(first), p =>
            {
                Assert.Equal(ActionMode.HoldReserve, p.Mode);
                Assert.Equal(0.0, p.DischargeKW, 6);
            });
        }

        [Fact]
        public void Below_the_reserve_the_whole_night_is_HoldReserve()
        {
            // 07-10 03:00: 486 Wh measured, reserve 810.
            var plan = Solve(0.486, _ => 140.0);

            Assert.All(plan.Plan, p => Assert.Equal(ActionMode.HoldReserve, p.Mode));
            Assert.All(plan.Plan, p => Assert.Equal(0.486, p.SocEndKWh, 3));
        }

        [Fact]
        public void Forecast_surplus_at_the_reserve_is_HoldReserve_too()
        {
            // A cloud turns the forecast surplus into a deficit: Zero Net Home would then
            // discharge below the reserve, HoldReserve cannot.
            var plan = Solve(0.81, i => i < 4 ? -200.0 : 150.0);

            Assert.Equal(ActionMode.HoldReserve, plan.Plan[0].Mode);
            Assert.True(plan.Plan[0].ChargeKW > 0.0, "surplus at the reserve should still be stored");
        }
    }
}
