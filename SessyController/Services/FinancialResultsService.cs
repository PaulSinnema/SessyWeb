using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class FinancialResultsService
    {
        private EnergyHistoryService _energyHistoryService { get; set; }

        public FinancialResultsService(EnergyHistoryService energyHistoryService)
        {
            _energyHistoryService = energyHistoryService;
        }

        public List<FinancialMonthResult> GetFinancialMonthResults(DateTime start, DateTime end)
        {
            var histories = _energyHistoryService.GetList((db) =>
            {
                return db.EnergyHistory
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
                    var revenue = netUsage * lastHistory.Price / 1000;

                    monthResult.FinancialResultsList.Add(new FinancialResult
                    {
                        Consumed = consumed1 + consumed2,
                        Produced = produced1 + produced2,
                        Price = lastHistory.Price,
                        Time = lastHistory.Time,
                        Cost = revenue
                    });
                }

                lastHistory = history;
            }

            return monthResults;
        }
    }
}
