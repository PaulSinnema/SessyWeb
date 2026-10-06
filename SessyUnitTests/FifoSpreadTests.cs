using SessyController.Services;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Spread column on the Planner analysis page (v1.0.148): each planned sale is paired by FIFO
    /// with the charge that fed it, and both quarters show the margin of that pair.
    /// </summary>
    public class FifoSpreadTests
    {
        private static readonly DateTime T0 = new(2026, 10, 6, 13, 0, 0);

        private static ChargeCostBasisService.ProjectionStep Step(int q, double socWh, double buy)
            => new(T0.AddMinutes(15 * q), socWh, 0.0, buy);

        private static PlannedQuarter Quarter(int q, double buy, double sell, double chargeW, double dischargeW, double netLoadWh = 0.0)
            => new()
            {
                Time = T0.AddMinutes(15 * q),
                BuyingPriceEurKWh = buy,
                SellingPriceEurKWh = sell,
                PlannedChargePowerW = chargeW,
                PlannedDischargePowerW = dischargeW,
                NetLoadWh = netLoadWh
            };

        [Fact]
        public void Charge_and_its_sale_share_the_pair_margin()
        {
            // q0 charges 1 kWh at 0.30, q1 empties it, exported at 0.50.
            var projection = ChargeCostBasisService.Project(
                [], [Step(0, 1000, 0.30), Step(1, 0, 0.45)], 0.0, 1.0, 1.0, T0);

            var c = Assert.Single(projection.Consumptions!);
            Assert.Equal(T0, c.ChargedAt);
            Assert.Equal(T0.AddMinutes(15), c.SoldAt);

            var spreads = PlannerAnalysisService.FifoSpreads(
                [Quarter(0, 0.30, 0.25, 4000, 0), Quarter(1, 0.45, 0.50, 0, 4000)], projection.Consumptions);

            Assert.Equal(0.20, spreads[T0], 6);
            Assert.Equal(0.20, spreads[T0.AddMinutes(15)], 6);
        }

        [Fact]
        public void Old_stock_is_sold_first_and_only_the_rest_credits_the_charge()
        {
            // 500 Wh stock at 0.10 from before the plan, then 1 kWh charged at 0.30, then 750 Wh sold
            // at 0.50: 500 Wh stock (margin 0.40) + 250 Wh of q0 (margin 0.20).
            var seed = new List<ChargeCostBasisService.CostBasisLayerInfo> { new(0, 500, 0.10, 0.10, false) };
            var projection = ChargeCostBasisService.Project(
                seed, [Step(0, 1500, 0.30), Step(1, 750, 0.45)], 500.0, 1.0, 1.0, T0);

            var spreads = PlannerAnalysisService.FifoSpreads(
                [Quarter(0, 0.30, 0.25, 4000, 0), Quarter(1, 0.45, 0.50, 0, 3000)], projection.Consumptions);

            Assert.Equal(0.20, spreads[T0], 6);
            Assert.Equal((500 * 0.40 + 250 * 0.20) / 750.0, spreads[T0.AddMinutes(15)], 6);
        }

        [Fact]
        public void Covering_the_house_is_valued_at_the_buy_price()
        {
            // 1 kWh delivered, 600 Wh of it covers the house (avoided 0.45), 400 Wh exported at 0.40.
            var projection = ChargeCostBasisService.Project(
                [], [Step(0, 1000, 0.20), Step(1, 0, 0.45)], 0.0, 1.0, 1.0, T0);

            var spreads = PlannerAnalysisService.FifoSpreads(
                [Quarter(0, 0.20, 0.15, 4000, 0), Quarter(1, 0.45, 0.40, 0, 4000, netLoadWh: 600)], projection.Consumptions);

            Assert.Equal(0.6 * 0.45 + 0.4 * 0.40 - 0.20, spreads[T0], 6);
        }

        [Fact]
        public void Energy_not_sold_within_the_plan_has_no_spread()
        {
            var projection = ChargeCostBasisService.Project([], [Step(0, 1000, 0.30), Step(1, 1000, 0.45)], 0.0, 1.0, 1.0, T0);

            var spreads = PlannerAnalysisService.FifoSpreads([Quarter(0, 0.30, 0.25, 4000, 0)], projection.Consumptions);

            Assert.Empty(spreads);
        }
    }
}
