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

        /// <summary>
        /// Calculate the price including overhead cost.
        /// Returns null if prices, taxes or overhead cost are missing.
        /// </summary>
        public double? CalculateEnergyPrice(DateTime date, bool buying)
        {
            var epexPriceRecord = _epexPricesDataService.Get((set) => set.FirstOrDefault(ep => ep.Time == date));
            var taxRecord = _taxesDataService.GetTaxesForDate(date);

            if (epexPriceRecord != null && taxRecord != null && epexPriceRecord.Price.HasValue)
            {
                var compensation = buying ? taxRecord.PurchaseCompensation : taxRecord.ReturnDeliveryCompensation;
                var valueAddedTaxFactor = taxRecord.ValueAddedTax / 100 + 1;
                var overheadCost = GetOverheadCost(date, taxRecord);

                if (overheadCost != null)
                {
                    return (epexPriceRecord.Price.Value +
                        overheadCost +
                        taxRecord.EnergyTax +
                        compensation) *
                        valueAddedTaxFactor;
                }
            }

            return null;
        }

        /// <summary>
        /// Calculate the overhead cost per hour for the date .
        /// </summary>
        private double? GetOverheadCost(DateTime date, Taxes taxRecord)
        {
            var hours = (new DateTime(date.Year, 12, 31) - new DateTime(date.Year, 1, 1)).Days * 24;

            var totalCost = taxRecord.NetManagementCost + taxRecord.FixedTransportFee + taxRecord.CapacityTransportFee - taxRecord.TaxReduction;

            return totalCost / hours;
        }
    }
}
