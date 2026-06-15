using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    public class EnergyPriceCalculatorTests
    {
        // Realistic Dutch tariff components.
        private const double Market = 0.10;          // EPEX market price
        private const double EnergyTax = 0.09157;    // energiebelasting
        private const double PurchaseComp = 0.0182;  // inkoopvergoeding
        private const double ReturnComp = -0.0182;   // verkoopvergoeding (stored negative)
        private const double Vat = 1.21;             // 21% BTW

        // ── Buying ───────────────────────────────────────────────────────────

        [Fact]
        public void Buying_WithNetting_IncludesTaxAndVat()
        {
            // (market + energyTax + purchaseComp) * VAT
            double expected = (Market + EnergyTax + PurchaseComp) * Vat;

            double actual = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, PurchaseComp, Vat, buying: true, netting: true);

            Assert.Equal(expected, actual, 6);
        }

        [Fact]
        public void Buying_WithoutNetting_Unchanged()
        {
            // Buying build-up is identical regardless of netting.
            double expected = (Market + EnergyTax + PurchaseComp) * Vat;

            double actual = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, PurchaseComp, Vat, buying: true, netting: false);

            Assert.Equal(expected, actual, 6);
        }

        [Fact]
        public void Buying_IncludesOverhead()
        {
            double overhead = 0.02;
            double expected = (Market + overhead + EnergyTax + PurchaseComp) * Vat;

            double actual = EnergyPriceCalculator.Calculate(
                Market, overhead, EnergyTax, PurchaseComp, Vat, buying: true, netting: true);

            Assert.Equal(expected, actual, 6);
        }

        // ── Selling with netting ──────────────────────────────────────────────

        [Fact]
        public void Selling_WithNetting_IncludesTaxAndVat()
        {
            // (market + energyTax + returnComp) * VAT — returnComp is negative.
            double expected = (Market + EnergyTax + ReturnComp) * Vat;

            double actual = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, ReturnComp, Vat, buying: false, netting: true);

            Assert.Equal(expected, actual, 6);
        }

        [Fact]
        public void Selling_WithNetting_RoughlyEqualsBuying()
        {
            // With netting, energy tax and VAT apply on both sides, so selling differs
            // from buying only by the compensation difference.
            double buy = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, PurchaseComp, Vat, buying: true, netting: true);
            double sell = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, ReturnComp, Vat, buying: false, netting: true);

            double expectedDiff = (PurchaseComp - ReturnComp) * Vat;
            Assert.Equal(expectedDiff, buy - sell, 6);
        }

        // ── Selling without netting ──────────────────────────────────────────

        [Fact]
        public void Selling_WithoutNetting_MarketMinusCompensationOnly()
        {
            // From 2027: market price minus return compensation. No tax, no overhead, no VAT.
            double expected = Market + ReturnComp;

            double actual = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, ReturnComp, Vat, buying: false, netting: false);

            Assert.Equal(expected, actual, 6);
        }

        [Fact]
        public void Selling_WithoutNetting_IgnoresOverheadAndVat()
        {
            // Overhead and VAT must have no effect on the post-netting feed-in tariff.
            double withOverhead = EnergyPriceCalculator.Calculate(
                Market, 0.05, EnergyTax, ReturnComp, Vat, buying: false, netting: false);

            Assert.Equal(Market + ReturnComp, withOverhead, 6);
        }

        [Fact]
        public void Selling_WithoutNetting_LowerThanWithNetting()
        {
            double withNetting = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, ReturnComp, Vat, buying: false, netting: true);
            double withoutNetting = EnergyPriceCalculator.Calculate(
                Market, 0.0, EnergyTax, ReturnComp, Vat, buying: false, netting: false);

            Assert.True(withoutNetting < withNetting,
                "Selling without netting should be lower (loses energy tax and VAT).");
        }
    }
}