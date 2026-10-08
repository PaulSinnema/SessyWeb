using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// v1.0.150: the house cover keeps the reserve each later quarter needs after the sun has
    /// refilled the battery (ReserveFloor), not the highest reserve anywhere in the horizon. With a
    /// calculated reserve the end of the horizon carries a full night reserve; holding that tonight
    /// left the battery idle overnight while tomorrow's sun would refill it anyway.
    /// </summary>
    public class ReserveFloorTests
    {
        private const double CapacityKWh = 16.2;
        private static readonly DateTime Start = new(2026, 10, 7, 21, 0, 0);

        private static PlanResult Solve(List<PricePoint> points, Func<DateTime, double> reserve, double initialSocKWh)
        {
            var bounds = points.Select(p => new SocBound(p.Start, reserve(p.Start), CapacityKWh)).ToList();
            var spec = new BatterySpec(CapacityKWh, initialSocKWh, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.90, DischargeEfficiency: 0.90);
            var opt = new SessyOptions(QuarterMinutes: 15, CycleCostEurPerKWh: 0.001, AllowExport: true);
            return BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;
        }

        [Fact]
        public void Tomorrows_evening_reserve_does_not_idle_the_battery_tonight()
        {
            // Night 150 Wh/quarter, sunny day 800 Wh surplus/quarter, evening again 150 Wh.
            var points = new List<PricePoint>();
            for (var t = Start; t < Start.Date.AddDays(2); t = t.AddMinutes(15))
            {
                bool sun = t >= Start.Date.AddDays(1).AddHours(7) && t < Start.Date.AddDays(1).AddHours(17);
                double net = sun ? -800.0 : 150.0;
                points.Add(new PricePoint(t, 0.30, 0.10, NetLoadWh: net, SolarSurplusWh: Math.Max(0.0, -net)));
            }

            // Calculated-reserve shape: small tonight, a full night reserve at the end of the horizon.
            var eveningTomorrow = Start.Date.AddDays(1).AddHours(17);
            var plan = Solve(points, t => t >= eveningTomorrow ? 4.6 : 0.5, initialSocKWh: 3.0);

            var first = plan.Plan[0];
            Assert.Equal(ActionMode.ZeroNetHome, first.Mode);
            Assert.Equal(0.150, first.DischargeKW * 0.25, 3);

            double lowestTonight = plan.Plan.Where(p => p.Start < Start.Date.AddDays(1).AddHours(7)).Min(p => p.SocEndKWh);
            Assert.True(lowestTonight < 1.0, $"battery held {lowestTonight:F2} kWh overnight for tomorrow evening");

            // And tomorrow evening's reserve is still met after the sun.
            Assert.All(plan.Plan.Where(p => p.Start >= eveningTomorrow),
                p => Assert.True(p.SocEndKWh >= 4.6 - 0.01, $"{p.Start:dd HH:mm} ends at {p.SocEndKWh:F2}"));
        }

        [Fact]
        public void A_later_reserve_without_sun_in_between_is_kept_from_now()
        {
            // No solar at all; a bridge reserve of 2,5 kWh five hours from now.
            var points = new List<PricePoint>();
            for (int i = 0; i < 24; i++)
                points.Add(new PricePoint(Start.AddMinutes(15 * i), 0.30, 0.10, NetLoadWh: 150.0, SolarSurplusWh: 0.0));

            var bridge = Start.AddMinutes(15 * 20);
            var plan = Solve(points, t => t == bridge ? 2.5 : 0.5, initialSocKWh: 3.0);

            Assert.All(plan.Plan.Where(p => p.Start <= bridge),
                p => Assert.True(p.SocEndKWh >= 2.5 - 0.01, $"{p.Start:HH:mm} ends at {p.SocEndKWh:F2} below the bridge reserve"));
            Assert.Contains(plan.Plan, p => p.Mode == ActionMode.ZeroNetHome);
            Assert.Contains(plan.Plan, p => p.Mode == ActionMode.HoldReserve);
        }

        [Fact]
        public void Plan_reports_its_reserve_floor_per_quarter()
        {
            var points = new List<PricePoint>();
            for (int i = 0; i < 8; i++)
                points.Add(new PricePoint(Start.AddMinutes(15 * i), 0.30, 0.10, NetLoadWh: 150.0, SolarSurplusWh: 0.0));

            var plan = Solve(points, t => t == Start.AddMinutes(15 * 6) ? 2.0 : 0.5, initialSocKWh: 3.0);

            Assert.All(plan.Plan.Take(7), p => Assert.Equal(2.0, p.ReserveFloorKWh, 6));
            Assert.Equal(0.5, plan.Plan[7].ReserveFloorKWh, 6);
        }
    }
}
