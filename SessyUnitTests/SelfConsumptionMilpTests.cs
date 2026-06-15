using SessyController.Services.Optimization;
using Xunit;

namespace SessyUnitTests
{
    public class SelfConsumptionMilpTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 6, 15, 0, 0, 0);

        private static BatterySpec Spec(double socKWh = 0.0) => new BatterySpec(
            CapacityKWh: 10.0,
            InitialSocKWh: socKWh,
            MaxChargeKW: 4.0,
            MaxDischargeKW: 4.0,
            ChargeEfficiency: 0.95,
            DischargeEfficiency: 0.95);

        private static SessyOptions Opt(double cycleCost = 0.01) => new SessyOptions(
            QuarterMinutes: 15,
            CycleCostEurPerKWh: cycleCost,
            TimeLimitMs: 5000);

        // netLoadWh negative = solar surplus; positive = household draw.
        private static List<PricePoint> Quarters((double buy, double sell, double netLoadWh)[] pts)
        {
            var list = new List<PricePoint>();
            for (int i = 0; i < pts.Length; i++)
                list.Add(new PricePoint(T0.AddMinutes(i * 15), pts[i].buy, pts[i].sell,
                    pts[i].netLoadWh, pts[i].netLoadWh < 0 ? -pts[i].netLoadWh : 0.0));
            return list;
        }

        private static List<SocBound> Bounds(IReadOnlyList<PricePoint> q, double cap = 10.0)
            => q.Select(p => new SocBound(p.Start, 0.0, cap)).ToList();

        [Fact]
        public void NeverExports_OnlyZeroNetHomeOrCharge()
        {
            // Expensive quarters, full battery: a profit solver would export, but the
            // self-consumption solver must only cover the house (ZeroNetHome).
            var q = Quarters(new[]
            {
                (0.40, 0.38, 100.0),
                (0.40, 0.38, 100.0),
            });

            var result = SelfConsumptionMilp.Solve(q, Spec(socKWh: 8.0), Opt(), Bounds(q));

            Assert.NotNull(result);
            Assert.DoesNotContain(result!.Plan, p => p.Mode == ActionMode.Discharge);
        }

        [Fact]
        public void StoresSolarSurplus_WhenBatteryHasRoom()
        {
            // Solar surplus now, expensive household draw later. Storing the free surplus
            // to avoid the expensive import beats curtailing it.
            var q = Quarters(new[]
            {
                (0.30, 0.05, -800.0),  // 800 Wh solar surplus
                (0.40, 0.38,  800.0),  // later expensive draw to cover
            });

            var result = SelfConsumptionMilp.Solve(q, Spec(socKWh: 0.0), Opt(), Bounds(q));

            Assert.NotNull(result);
            Assert.True(result!.Plan[0].ChargeKW > 0.0,
                "Expected charging to store the solar surplus for the later expensive quarter.");
        }

        [Fact]
        public void FeasibleWhenBatteryFull_AndSolarSurplus()
        {
            // Battery full and solar surplus that cannot be stored: must stay feasible
            // (surplus is curtailed), not throw or return null.
            var q = Quarters(new[]
            {
                (0.30, 0.05, -2000.0),
            });
            var bounds = q.Select(p => new SocBound(p.Start, 0.0, 10.0)).ToList();

            var result = SelfConsumptionMilp.Solve(q, Spec(socKWh: 10.0), Opt(), bounds);

            Assert.NotNull(result);
        }
    }
}