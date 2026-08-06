using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Selling stock into an expensive quarter and buying it back later.
    ///
    /// Candidate A may only sell what the SOC path can spare all the way to the end of the horizon.
    /// Energy the plan still needs later can therefore not be sold now, however well now pays: on
    /// the production plan of 06-08 only 551 Wh of the stock was sellable and the binding quarter
    /// was the very last one of the horizon, so the whole evening peak at €0,305 went untouched
    /// with 3,3 kWh in the battery — carried through the night and sold a day later, after buying
    /// more at €0,16.
    ///
    /// Pairing the sale with its repurchase changes which stretch has to stay above the reserve:
    /// only the dip between the two, instead of everything that follows.
    /// </summary>
    public class SellNowRebuyLaterTests
    {
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 6.6;
        private const double MaxDischargeKW = 5.1;
        private const double ReserveKWh = 6.0;

        private static readonly DateTime Start = new(2026, 8, 9, 20, 0, 0);

        private static SessyOptions Options() => new(QuarterMinutes: 15, CycleCostEurPerKWh: 0.01);

        private static BatterySpec Spec(double initialSocKWh) =>
            new(CapacityKWh, initialSocKWh, MaxChargeKW, MaxDischargeKW,
                ChargeEfficiency: 0.95, DischargeEfficiency: 0.95);

        /// <summary>
        /// The production shape: an expensive block, a cheap block, and then a household load that
        /// eats the battery down to the reserve by the end of the horizon. There is room to dip
        /// between the expensive and the cheap block, but none at all at the horizon's edge — so
        /// Candidate A, which needs slack everywhere after the sale, is blocked.
        /// </summary>
        private static (List<PricePoint> Prices, List<SocBound> Bounds) Scenario(
            double sellPrice, double rebuyPrice)
        {
            var prices = new List<PricePoint>();

            for (int i = 0; i < 4; i++)          // expensive: sell into this
                prices.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: sellPrice + 0.04, SellEurPerKWh: sellPrice,
                    NetLoadWh: 0.0, SolarSurplusWh: 0.0));

            for (int i = 4; i < 8; i++)          // cheap: buy it back here
                prices.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: rebuyPrice, SellEurPerKWh: rebuyPrice - 0.04,
                    NetLoadWh: 0.0, SolarSurplusWh: 0.0));

            for (int i = 8; i < 16; i++)         // household load drains to the reserve
                prices.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.22, SellEurPerKWh: 0.18,
                    NetLoadWh: 550.0, SolarSurplusWh: 0.0));

            var bounds = prices
                .Select(p => new SocBound(p.Start, ReserveKWh, CapacityKWh))
                .ToList();

            return (prices, bounds);
        }

        [Fact]
        public void Stock_the_tail_needs_is_still_sold_when_it_can_be_bought_back()
        {
            var (prices, bounds) = Scenario(sellPrice: 0.30, rebuyPrice: 0.16);

            var result = BatteryGreedyPlanner.Solve(prices, Spec(10.0), Options(), bounds);

            Assert.NotNull(result);

            double sold = result!.Plan.Take(4).Sum(p => p.DischargeKW) * 0.25;
            double boughtBack = result.Plan.Skip(4).Take(4).Sum(p => p.ChargeKW) * 0.25;

            Assert.True(sold > 0.5,
                $"expected the expensive block to be sold into, got {sold:F2} kWh");

            Assert.True(boughtBack > 0.5,
                $"expected the sale to be bought back in the cheap block, got {boughtBack:F2} kWh");
        }

        [Fact]
        public void The_dip_stays_above_the_reserve()
        {
            var (prices, bounds) = Scenario(sellPrice: 0.30, rebuyPrice: 0.16);

            var result = BatteryGreedyPlanner.Solve(prices, Spec(10.0), Options(), bounds);

            Assert.NotNull(result);

            // The trade is allowed because the repurchase restores the level, not because the
            // floor moved. Selling below the reserve is exactly what the reserve prevents.
            foreach (var step in result!.Plan)
                Assert.True(step.SocEndKWh >= ReserveKWh - 0.01,
                    $"SOC fell to {step.SocEndKWh:F2} kWh at {step.Start:HH:mm}, below the {ReserveKWh:F2} kWh reserve");
        }

        [Fact]
        public void No_trade_when_buying_it_back_costs_more_than_the_sale()
        {
            // Sell at 0.16, buy back at 0.30: the round trip loses money and nothing should move
            // beyond what the household itself needs.
            var (prices, bounds) = Scenario(sellPrice: 0.16, rebuyPrice: 0.30);

            var result = BatteryGreedyPlanner.Solve(prices, Spec(10.0), Options(), bounds);

            Assert.NotNull(result);

            double sold = result!.Plan.Take(4).Sum(p => p.DischargeKW) * 0.25;

            Assert.True(sold < 0.01, $"nothing should be sold into a losing round trip, got {sold:F2} kWh");
        }

        [Fact]
        public void Nothing_is_sold_when_there_is_no_room_to_dip_at_all()
        {
            // Battery exactly on the reserve: there is no slack anywhere, so refusing to trade is
            // the correct answer and the pairing must not invent room.
            var (prices, bounds) = Scenario(sellPrice: 0.30, rebuyPrice: 0.16);

            var result = BatteryGreedyPlanner.Solve(prices, Spec(ReserveKWh), Options(), bounds);

            Assert.NotNull(result);

            double sold = result!.Plan.Take(4).Sum(p => p.DischargeKW) * 0.25;

            Assert.True(sold < 0.01, $"expected no sale without slack, got {sold:F2} kWh");
        }
    }
}
