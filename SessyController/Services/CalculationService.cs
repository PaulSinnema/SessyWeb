using SessyController.Services.Items;
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

        private static Taxes? _taxes { get; set; } = null;
        private static EPEXPrices? _epexPrice { get; set; } = null;

        /// <summary>
        /// Calculate the price including overhead cost.
        /// Returns null if prices, taxes or overhead cost are missing.
        /// </summary>
        public double? CalculateEnergyPrice(DateTime time, bool buying, bool includeOverheadCosts = false)
        {
            if (_epexPrice == null || _epexPrice.Time != time)
                _epexPrice = _epexPricesDataService.Get((set) => set.FirstOrDefault(ep => ep.Time == time));

            if (_taxes == null || _taxes.Time != time)
                _taxes = _taxesDataService.GetTaxesForDate(time);

            if (_epexPrice != null && _taxes != null && _epexPrice.Price.HasValue)
            {
                var compensation = buying ? _taxes.PurchaseCompensation : _taxes.ReturnDeliveryCompensation;
                var valueAddedTaxFactor = _taxes.ValueAddedTax / 100 + 1;

                var overheadCost = 0.0; 
                
                if (includeOverheadCosts)
                    overheadCost = GetOverheadCost(time, _taxes) ?? 0.0;

                return (_epexPrice.Price + overheadCost + _taxes.EnergyTax + compensation) * valueAddedTaxFactor;
            }

            return null;
        }

        /// <summary>
        /// Calculate the overhead cost per hour for the date .
        /// </summary>
        private double? GetOverheadCost(DateTime date, Taxes taxRecord)
        {
            var hours = (new DateTime(date.Year, 12, 31) - new DateTime(date.Year, 1, 1)).Days * 24;

            var totalCost = taxRecord.NetManagementCost + taxRecord.FixedTransportFee + taxRecord.CapacityTransportFee;

            totalCost -= taxRecord.TaxReduction;

            return totalCost / hours;
        }
    }
}
