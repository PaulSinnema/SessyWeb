using SessyData.Model;

namespace SessyController.Services
{
    /// <summary>
    /// Pure, side-effect-free energy price formulas. Kept separate from CalculationService
    /// (which handles caching and data access) so the price rules can be unit-tested
    /// directly and are defined in exactly one place.
    ///
    /// Buying (always):
    ///   (marketPrice + overhead + energyTax + purchaseCompensation) * vatFactor
    ///
    /// Selling WITH netting (until 01-01-2027):
    ///   (marketPrice + overhead + energyTax + returnCompensation) * vatFactor
    ///   (returnCompensation is stored as a negative value)
    ///
    /// Selling WITHOUT netting (from 01-01-2027):
    ///   marketPrice + returnCompensation
    ///   (no energy tax, no overhead, no VAT)
    ///
    /// Solar bonus (e.g. Zonneplan's Zonnebonus): while selling, in daylight and the market price
    /// is positive, (marketPrice + compensation) * solarBonusPercentage / 100 is added to the
    /// compensation before either formula above runs — Zonneplan's own example (market price
    /// €0.080 + €0.02 return compensation = €0.100, 10% bonus = €0.010) bonuses the combined
    /// amount, not the market price alone. 0% (the default) leaves both formulas unchanged.
    /// </summary>
    public static class EnergyPriceCalculator
    {
        /// <summary>Buying price (EUR/kWh) at the given tariffs.</summary>
        public static double Buy(Taxes taxes, double marketPrice, double overhead = 0.0)
            => Calculate(marketPrice, overhead, taxes.EnergyTax, taxes.PurchaseCompensation, taxes.VatFactor,
                         buying: true, netting: taxes.Netting);

        /// <summary>Selling price (EUR/kWh) at the given tariffs, incl. the Zonnebonus in daylight.</summary>
        public static double Sell(Taxes taxes, double marketPrice, bool isDaylight, double overhead = 0.0)
            => Calculate(marketPrice, overhead, taxes.EnergyTax, taxes.SignedReturnDeliveryCompensation, taxes.VatFactor,
                         buying: false, netting: taxes.Netting, taxes.SolarBonusPercentage, isDaylight);

        /// <summary>
        /// Return compensation incl. the Zonnebonus: while selling in daylight and the market price is positive,
        /// (marketPrice + compensation) * solarBonusPercentage / 100 is added.
        /// </summary>
        public static double SellCompensation(double marketPrice, double compensation, double solarBonusPercentage, bool isDaylight)
            => isDaylight && marketPrice > 0.0 && solarBonusPercentage > 0.0
                ? compensation + (marketPrice + compensation) * solarBonusPercentage / 100.0
                : compensation;

        /// <param name="marketPrice">EPEX market price (EUR/kWh).</param>
        /// <param name="overhead">Supplier overhead cost (EUR/kWh), 0 when not included.</param>
        /// <param name="energyTax">Energy tax (EUR/kWh).</param>
        /// <param name="compensation">Purchase compensation (buying) or return-delivery
        /// compensation (selling, stored negative).</param>
        /// <param name="vatFactor">1 + VAT fraction (e.g. 1.21).</param>
        /// <param name="buying">True for the buying price, false for the selling price.</param>
        /// <param name="netting">True while net-metering applies.</param>
        /// <param name="solarBonusPercentage">Zonnebonus percentage on top of (marketPrice + compensation) (0 = none).</param>
        /// <param name="isDaylight">True when it's between sunrise and sunset.</param>
        public static double Calculate(
            double marketPrice,
            double overhead,
            double energyTax,
            double compensation,
            double vatFactor,
            bool buying,
            bool netting,
            double solarBonusPercentage = 0.0,
            bool isDaylight = false)
        {
            if (!buying)
                compensation = SellCompensation(marketPrice, compensation, solarBonusPercentage, isDaylight);

            if (buying)
                return (marketPrice + overhead + energyTax + compensation) * vatFactor;

            // Selling without netting: market price minus return compensation only.
            if (!netting)
                return marketPrice + compensation;

            // Selling with netting: same build-up as buying, incl. energy tax and VAT.
            return (marketPrice + overhead + energyTax + compensation) * vatFactor;
        }
    }
}