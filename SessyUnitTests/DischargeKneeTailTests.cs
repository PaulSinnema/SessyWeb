using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Regression for 06-10 13:07: charging stopped at ~8 kWh while the evening peak could take
    /// far more. Below the discharge knee each quarter delivers only a slice of a higher SOC, and
    /// Candidate B credited a single discharge quarter, so extra charge never looked worth it.
    /// </summary>
    public class DischargeKneeTailTests
    {
        private readonly ITestOutputHelper _out;
        public DischargeKneeTailTests(ITestOutputHelper output) => _out = output;

        private const double CapacityKWh = 16.2;
        private const double ReserveKWh = 0.81;

        private static readonly DateTime Start = new(2026, 10, 6, 13, 0, 0);
        private static readonly DateTime PeakStart = Start.Date.AddHours(17).AddMinutes(45);
        private static readonly DateTime PeakEnd = Start.Date.AddHours(20).AddMinutes(15);

        // Same shape as 06-10: ~0.30 afternoon, 0.60 evening peak, no solar.
        private static List<PricePoint> Points()
        {
            var points = new List<PricePoint>();
            for (var t = Start; t < Start.Date.AddDays(1); t = t.AddMinutes(15))
            {
                (double buy, double sell) =
                    t < Start.Date.AddHours(14).AddMinutes(15) ? (0.295, 0.251) :
                    t < Start.Date.AddHours(15) ? (0.313, 0.269) :
                    t < PeakStart ? (0.32, 0.276) :
                    t < PeakEnd ? (0.60, 0.556) :
                    (0.42, 0.376);

                points.Add(new PricePoint(t, buy, sell, NetLoadWh: 150.0, SolarSurplusWh: 0.0));
            }
            return points;
        }

        private static PlanResult Solve(DischargeCapability capability)
        {
            var points = Points();
            var bounds = points.Select(p => new SocBound(p.Start, ReserveKWh, CapacityKWh)).ToList();
            var spec = new BatterySpec(CapacityKWh, 0.467, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.87, DischargeEfficiency: 0.90,
                DischargeCapability: capability);
            var opt = new SessyOptions(QuarterMinutes: 15, CycleCostEurPerKWh: 0.001, AllowExport: true);

            return BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;
        }

        private static double ChargedBeforePeakKWh(PlanResult plan) =>
            plan.Plan.Where(p => p.Start < PeakStart).Sum(p => p.ChargeKW * 0.25);

        private static double DischargedInPeakKWh(PlanResult plan) =>
            plan.Plan.Where(p => p.Start >= PeakStart && p.Start < PeakEnd).Sum(p => p.DischargeKW * 0.25);

        [Fact]
        public void Knee_does_not_stop_charging_for_the_peak()
        {
            var plan = Solve(new DischargeCapability(PlateauW: 4669, KneeSoc: 0.30, Samples: 20));
            _out.WriteLine(string.Join(" ", plan.Plan.Where(p => p.Start.Hour < 21)
                .Select(p => $"{p.Start:HHmm}:{(p.ChargeKW - p.DischargeKW) * 1000:F0}/{p.SocEndKWh * 1000:F0}")));

            // v1.0.145: 9.7 kWh charged, search ran into MaxIterations.
            Assert.True(ChargedBeforePeakKWh(plan) > 11.0,
                $"only {ChargedBeforePeakKWh(plan):F2} kWh charged before the peak");
            Assert.True(DischargedInPeakKWh(plan) > 9.0,
                $"only {DischargedInPeakKWh(plan):F2} kWh discharged in the peak");
        }

        [Fact]
        public void Tail_discharge_stays_within_the_knee_cap()
        {
            var capability = new DischargeCapability(PlateauW: 4669, KneeSoc: 0.30, Samples: 20);
            var plan = Solve(capability);

            // Small slack for the rebuild's power-dependent efficiency.
            foreach (var step in plan.Plan)
            {
                double capKW = Math.Min(5.1, capability.PowerW(step.SocStartKWh / CapacityKWh) / 1000.0);
                Assert.True(step.DischargeKW <= capKW + 0.05,
                    $"{step.Start:HH:mm}: {step.DischargeKW:F2} kW above cap {capKW:F2} kW at SOC {step.SocStartKWh:F2}");
            }
        }
    }
}
