using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Regression for 06-10: the battery ran empty at 12:45 on a cloudy day and the greedy plan
    /// stayed idle through the evening peak. Below the discharge knee the cap is 0 at SOC 0, and
    /// Candidate B read that cap on the path before its own charge, so no first block ever fit.
    /// </summary>
    public class EmptyBatteryDeadlockTests
    {
        private const double CapacityKWh = 16.2;
        private const double ReserveKWh = 0.81;

        private static readonly DateTime Start = new(2026, 10, 6, 13, 0, 0);

        // Same shape as 06-10: cheap afternoon, expensive evening peak, no solar.
        private static List<PricePoint> Points()
        {
            var points = new List<PricePoint>();
            for (var t = Start; t < Start.Date.AddDays(1); t = t.AddMinutes(15))
            {
                (double buy, double sell) =
                    t.Hour < 17 ? (0.30, 0.256) :
                    t < Start.Date.AddHours(17).AddMinutes(45) ? (0.40, 0.356) :
                    t < Start.Date.AddHours(20).AddMinutes(15) ? (0.60, 0.556) :
                    (0.42, 0.376);

                points.Add(new PricePoint(t, buy, sell, NetLoadWh: 150.0, SolarSurplusWh: 0.0));
            }
            return points;
        }

        private static List<SocBound> Bounds(IEnumerable<PricePoint> points) =>
            points.Select(p => new SocBound(p.Start, ReserveKWh, CapacityKWh)).ToList();

        private static PlanResult Solve(double initialSocKWh)
        {
            var points = Points();
            var spec = new BatterySpec(CapacityKWh, initialSocKWh, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.95, DischargeEfficiency: 0.95,
                DischargeCapability: new DischargeCapability(PlateauW: 4121, KneeSoc: 0.20, Samples: 748));
            var opt = new SessyOptions(QuarterMinutes: 15, CycleCostEurPerKWh: 0.001, AllowExport: true);

            return BatteryGreedyPlanner.Solve(points, spec, opt, Bounds(points))!;
        }

        private static double ChargedBeforePeakKWh(PlanResult plan) =>
            plan.Plan.Where(p => p.Start.Hour < 17).Sum(p => p.ChargeKW * 0.25);

        private static double DischargedInPeakKWh(PlanResult plan) =>
            plan.Plan.Where(p => p.Start >= Start.Date.AddHours(17).AddMinutes(45)
                              && p.Start < Start.Date.AddHours(20).AddMinutes(15))
                     .Sum(p => p.DischargeKW * 0.25);

        [Fact]
        public void Empty_battery_still_charges_cheap_and_discharges_into_the_peak()
        {
            var plan = Solve(initialSocKWh: 0.0);

            Assert.True(ChargedBeforePeakKWh(plan) > 1.0,
                $"no afternoon charging planned ({ChargedBeforePeakKWh(plan):F2} kWh) — empty-battery deadlock");
            Assert.True(DischargedInPeakKWh(plan) > 1.0,
                $"evening peak left untouched ({DischargedInPeakKWh(plan):F2} kWh) — empty-battery deadlock");
        }

        [Fact]
        public void Empty_and_almost_empty_battery_plan_alike()
        {
            // 216 Wh was the SOC one solve earlier on 06-10. The plans may differ by roughly the
            // value of that energy, not by the whole day's arbitrage.
            var empty = Solve(initialSocKWh: 0.0);
            var almostEmpty = Solve(initialSocKWh: 0.216);

            double gap = almostEmpty.ObjectiveEur - empty.ObjectiveEur;
            Assert.True(Math.Abs(gap) < 0.5,
                $"objective jumps by {gap:F2} EUR between SOC 0.216 and 0 kWh");
        }
    }
}
