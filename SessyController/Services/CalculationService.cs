using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class CalculationService
    {
        private EPEXPricesDataService _epexPricesDataService { get; set; }
        private TaxesDataService _taxesDataService { get; set; }
        private EnergyHistoryService _energyHistoryService { get; set; }

        public CalculationService(EPEXPricesDataService epexPricesDataService,
                                  TaxesDataService taxesDataService,
                                  EnergyHistoryService energyHistoryService)
        {
            _epexPricesDataService = epexPricesDataService;
            _taxesDataService = taxesDataService;
            _energyHistoryService = energyHistoryService;
        }

        private static EPEXPrices? _epexPrice { get; set; } = null;

        /// <summary>
        /// Calculate the price including overhead cost.
        /// Returns null if prices, taxes or overhead cost are missing.
        /// </summary>
        public async Task<double?> CalculateEnergyPrice(DateTime time, bool buying, bool includeOverheadCosts = false)
        {
            if (_epexPrice == null || _epexPrice.Time != time)
                _epexPrice = await _epexPricesDataService.Get(async (set) =>
                {
                    var result = set.FirstOrDefault(ep => ep.Time == time);

                    return await Task.FromResult(result);
                });

            var taxes = await _taxesDataService.GetTaxesForDate(time);

            if (_epexPrice != null && taxes != null && _epexPrice.Price.HasValue)
            {
                var compensation = buying ? taxes.PurchaseCompensation : -taxes.ReturnDeliveryCompensation;
                var valueAddedTaxFactor = taxes.ValueAddedTax / 100 + 1;

                var overheadCost = 0.0;

                if (includeOverheadCosts)
                    overheadCost = GetOverheadCost(time, taxes) ?? 0.0;

                var energyTax = 0.0;

                if (taxes.Netting)
                {
                    energyTax = taxes.EnergyTax;
                }
                else
                {
                    if (buying)
                    {
                        energyTax = taxes.EnergyTax;
                    }
                }

                return (_epexPrice.Price + overheadCost + energyTax + compensation) * valueAddedTaxFactor;
            }

            return null;
        }

        /// <summary>
        /// Calculate the overhead cost per quarter for the date.
        /// To determine if prices become negative these costs should NOT be included.
        /// </summary>
        private double? GetOverheadCost(DateTime date, Taxes taxRecord)
        {
            var quarters = (new DateTime(date.Year + 1, 1, 1) - new DateTime(date.Year, 1, 1)).Days * 24 * 4;

            var totalCost = taxRecord.NetManagementCost + taxRecord.FixedTransportFee + taxRecord.CapacityTransportFee;

            totalCost -= taxRecord.TaxReduction;

            return totalCost / quarters;
        }
    }
}
