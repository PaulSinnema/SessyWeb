using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class FinancialResultsService
    {
        private EnergyHistoryService _energyHistoryService { get; set; }

        private TaxesService _taxesService;

        public FinancialResultsService(EnergyHistoryService energyHistoryService, TaxesService taxesService)
        {
            _energyHistoryService = energyHistoryService;
            _taxesService = taxesService;
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
                    var consumed1 = history.ConsumedTariff1 - lastHistory.ConsumedTariff1;
                    var consumed2 = history.ConsumedTariff2 - lastHistory.ConsumedTariff2;
                    var produced1 = history.ProducedTariff1 - lastHistory.ProducedTariff1;
                    var produced2 = history.ProducedTariff2 - lastHistory.ProducedTariff2;

                    var netUsage = (produced1 + produced2) - (consumed1 + consumed2);
                    var price = GetTaxedPrice(lastHistory);
                    var revenue = netUsage * price / 1000;

                    monthResult.FinancialResultsList.Add(new FinancialResult
                    {
                        Consumed = consumed1 + consumed2,
                        Produced = produced1 + produced2,
                        Price = price,
                        Time = lastHistory.Time,
                        Cost = revenue
                    });
                }

                lastHistory = history;
            }

            return monthResults;
        }

        /// <summary>
        /// Returns the taxed price.
        /// </summary>
        private double GetTaxedPrice(EnergyHistory history)
        {
            Taxes? taxes = _taxesService.GetTaxesForDate(history.Time);

            if(taxes == null) throw new InvalidOperationException($"There is no valid tax record for date {history.Time}");

            var price = history.Price + taxes.EnergyTax;

            return price + (price * taxes.ValueAddedTax / 100);
        }
    }
}
