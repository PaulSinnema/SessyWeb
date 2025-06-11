using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class FinancialResultsService
    {
        private EnergyHistoryService _energyHistoryService { get; set; }

        private TaxesDataService _taxesService { get; set; }
        private CalculationService _calculationService { get; set; }

        public FinancialResultsService(EnergyHistoryService energyHistoryService, TaxesDataService taxesService, CalculationService calculationService)
        {
            _energyHistoryService = energyHistoryService;
            _taxesService = taxesService;
            _calculationService = calculationService;
        }

        public List<FinancialMonthResult> GetFinancialMonthResults(DateTime start, DateTime end)
        {
            var histories = _energyHistoryService.GetList((set) =>
            {
                return set
                    .Where(eh => eh.Time >= start && eh.Time <= end)
                    .OrderBy(eh => eh.Time)
                    .ToList();
            });

            EnergyHistory? lastHistory = null;

            List<FinancialMonthResult>? monthResults = new();

            foreach (var history in histories)
            {
                var monthResult = monthResults.Where(mr => mr.Year == history.Time.Year && mr.Month == history.Time.Month).FirstOrDefault();

                if(monthResult == null)
                {
                    monthResult = new FinancialMonthResult { Year = history.Time.Year, Month = history.Time.Month };
                    monthResults.Add(monthResult);
                }

                if(lastHistory != null)
                {
                    var gridPower = new GridPower(history, lastHistory);

                    var netUsage = gridPower.TotalInversed;
                    var price = GetTaxedPrice(lastHistory, gridPower);
                    var cost = (decimal)netUsage * price / 1000;

                    monthResult.FinancialResultsList.Add(new FinancialResult
                    {
                        Consumed = gridPower.TotalConsumed,
                        Produced = gridPower.TotalProduced,
                        Price = price,
                        Time = lastHistory.Time,
                        Cost = cost
                    });
                }

                lastHistory = history;
            }

            return monthResults;
        }

        /// <summary>
        /// Returns the taxed price.
        /// </summary>
        private decimal GetTaxedPrice(EnergyHistory history, GridPower gridPower)
        {
            var price = _calculationService.CalculateEnergyPrice(history.Time, gridPower.IsConsumer);

            if(price != null)
            {
                return (decimal)price.Value;
            }

            return 0;

            //Taxes? taxes = _taxesService.GetTaxesForDate(history.Time);

            //if(taxes == null) throw new InvalidOperationException($"There is no valid tax record for date {history.Time}");

            //decimal overheadCost = gridPower.IsConsumer ? (decimal)taxes.PurchaseCompensation : (decimal)taxes.ReturnDeliveryCompensation;

            //decimal price = (decimal)history.Price + (decimal)taxes.EnergyTax + overheadCost;

            //return price * (1 + (decimal)taxes.ValueAddedTax / (decimal)100);
        }
    }
}
