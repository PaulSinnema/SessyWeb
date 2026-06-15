using SessyController.Services.Optimization;
using Xunit;

namespace SessyUnitTests
{
    public class BatteryArbitrageMilpTests
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

        // Build quarters with a flat household load and given buy/sell prices.
        private static List<PricePoint> Quarters(
            (double buy, double sell)[] prices, double loadKw = 0.4)
        {
            var list = new List<PricePoint>();
            for (int i = 0; i < prices.Length; i++)
            {
                double netLoadWh = loadKw * 1000.0 * 0.25; // kW → Wh per quarter
                list.Add(new PricePoint(
                    T0.AddMinutes(i * 15),
                    prices[i].buy,
                    prices[i].sell,
                    netLoadWh,
                    0.0));
            }
            return list;
        }

        private static List<SocBound> Bounds(IReadOnlyList<PricePoint> q, double cap = 10.0)
            => q.Select(p => new SocBound(p.Start, 0.0, cap)).ToList();

        [Fact]
        public void ChargesWhenCheap_DischargesWhenExpensive()
        {
            // Two cheap quarters then two expensive ones.
            var q = Quarters(new[]
            {
                (0.10, 0.09), (0.10, 0.09),
                (0.40, 0.38), (0.40, 0.38),
            });

            var result = BatteryArbitrageMilp.Solve(q, Spec(socKWh: 0.0), Opt(), Bounds(q));

            Assert.NotNull(result);
            // Charges in at least one cheap quarter.
            Assert.Contains(result!.Plan.Take(2), p => p.Mode == ActionMode.Charge);
            // Discharges (self-use or export) in at least one expensive quarter.
            Assert.Contains(result.Plan.Skip(2),
                p => p.Mode == ActionMode.Discharge || p.Mode == ActionMode.ZeroNetHome);
        }

        [Fact]
        public void PrefersHoldingOverLowExport_WithCostBasis()
        {
            // Battery starts full with a cost basis of 0.25/kWh (water value). A single
            // quarter with a low sell price (0.20) should not trigger a full export dump:
            // holding the energy (worth 0.25) beats exporting it (0.20). So discharge stays
            // small — at most covering the house, never a 4 kW export.
            var q = Quarters(new[] { (0.40, 0.20) }, loadKw: 0.4);
            var opt = new SessyOptions(
                QuarterMinutes: 15,
                CycleCostEurPerKWh: 0.01,
                TimeLimitMs: 5000,
                BeginSocCostEurPerKWh: 0.25);

            var result = BatteryArbitrageMilp.Solve(q, Spec(socKWh: 8.0), opt, Bounds(q));

            Assert.NotNull(result);
            // No export means the quarter is not classified as Discharge (export mode).
            Assert.NotEqual(ActionMode.Discharge, result!.Plan[0].Mode);
        }

        [Fact]
        public void ExportsSurplus_WhenSellExceedsCost()
        {
            // Battery full, very high sell price, tiny load: exporting is worthwhile.
            var q = Quarters(new[] { (0.50, 0.50) }, loadKw: 0.1);
            var result = BatteryArbitrageMilp.Solve(q, Spec(socKWh: 9.0), Opt(), Bounds(q));

            Assert.NotNull(result);
            // Discharge well above the small load means surplus is exported.
            Assert.True(result!.Plan[0].DischargeKW > 0.5,
                "Expected export discharge above household load at a high sell price.");
        }

        [Fact]
        public void RespectsMinSocReserve()
        {
            // High prices everywhere but a reserve floor of 5 kWh; starting at 5 kWh the
            // battery may not discharge below the floor.
            var q = Quarters(new[] { (0.40, 0.38), (0.40, 0.38) });
            var bounds = q.Select(p => new SocBound(p.Start, 5.0, 10.0)).ToList();

            var result = BatteryArbitrageMilp.Solve(q, Spec(socKWh: 5.0), Opt(), bounds);

            Assert.NotNull(result);
            Assert.All(result!.Plan, p => Assert.True(p.SocEndKWh >= 5.0 - 1e-6,
                $"SOC {p.SocEndKWh:F3} dropped below reserve 5.0."));
        }

        [Fact]
        public void IdleWhenFlatPrices_NoProfitableTrade()
        {
            // Flat prices with spread below cycle cost → no arbitrage, battery just
            // covers the house (ZeroNetHome) or stays idle, never charges from grid.
            var q = Quarters(new[] { (0.20, 0.19), (0.20, 0.19), (0.20, 0.19) });
            var result = BatteryArbitrageMilp.Solve(q, Spec(socKWh: 0.0), Opt(cycleCost: 0.10), Bounds(q));

            Assert.NotNull(result);
            Assert.DoesNotContain(result!.Plan, p => p.Mode == ActionMode.Charge);
        }
    }
}