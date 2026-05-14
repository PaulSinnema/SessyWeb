using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class FinancialResultsService
    {
        private readonly QuarterlyMeasurementDataService _measurementService;
        private readonly CalculationService _calculationService;

        public FinancialResultsService(
            QuarterlyMeasurementDataService measurementService,
            CalculationService calculationService)
        {
            _measurementService = measurementService;
            _calculationService = calculationService;
        }

        public async Task<decimal> GetFinancialResultForDate(DateTime date)
        {
            var start = date.Date;
            var end = start.AddDays(1);

            var results = await GetFinancialMonthResults(start, end);

            return results.Sum(r => r.TotalCost);
        }

        public async Task<IQueryable<FinancialMonthResult>> GetFinancialMonthResults(
            DateTime start, DateTime end)
        {
            var measurements = await _measurementService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= start && m.Time < end)
                    .OrderBy(m => m.Time)
                    .ToList();

                return await Task.FromResult(result);
            });

            var monthResults = new List<FinancialMonthResult>();

            foreach (var m in measurements)
            {
                var monthResult = monthResults
                    .FirstOrDefault(mr => mr.Year == m.Time.Year && mr.Month == m.Time.Month);

                if (monthResult == null)
                {
                    monthResult = new FinancialMonthResult
                    {
                        Year = m.Time.Year,
                        Month = m.Time.Month
                    };
                    monthResults.Add(monthResult);
                }

                // GridImportWh > 0: consumed from grid → positive cost.
                // GridExportWh > 0: produced to grid → negative cost (revenue).
                double netWh = m.GridImportWh - m.GridExportWh;
                bool isConsumer = netWh >= 0;

                var price = await _calculationService.CalculateEnergyPrice(m.Time, isConsumer);
                var cost = (decimal)(netWh * (price ?? 0.0) / 1000.0);

                monthResult.FinancialResultsList.Add(new FinancialResult
                {
                    Consumed = m.GridImportWh,
                    Produced = m.GridExportWh,
                    Price = (decimal)(price ?? 0.0),
                    Time = m.Time,
                    Cost = cost
                });
            }

            return monthResults.AsQueryable();
        }
    }
}