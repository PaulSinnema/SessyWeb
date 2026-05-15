using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Calculates financial results per quarter-hour using two data sources:
    ///
    /// 1. EnergyHistory (archive): P1 cumulative meter readings from March 2025 to
    ///    May 2026. Grid flows are derived as deltas between consecutive readings.
    ///    Interval ~60 minutes.
    ///
    /// 2. QuarterlyMeasurements (current): per-quarter grid import/export deltas
    ///    stored directly in Wh from May 2026 onwards. Interval 15 minutes.
    ///
    /// Results from both sources are merged and sorted by time.
    /// </summary>
    public class FinancialResultsService
    {
        private readonly EnergyHistoryDataService _energyHistoryService;
        private readonly QuarterlyMeasurementDataService _measurementService;
        private readonly CalculationService _calculationService;

        public FinancialResultsService(
            EnergyHistoryDataService energyHistoryService,
            QuarterlyMeasurementDataService measurementService,
            CalculationService calculationService)
        {
            _energyHistoryService = energyHistoryService;
            _measurementService = measurementService;
            _calculationService = calculationService;
        }

        public async Task<decimal> GetFinancialResultForDate(DateTime date)
        {
            var results = await GetFinancialMonthResults(date.Date, date.Date.AddDays(1));
            return results.Sum(r => r.TotalCost);
        }

        public async Task<IQueryable<FinancialMonthResult>> GetFinancialMonthResults(
            DateTime start, DateTime end)
        {
            var monthResults = new List<FinancialMonthResult>();

            // ── Source 1: EnergyHistory archive ──────────────────────────────
            var histories = await _energyHistoryService.GetList(async set =>
            {
                var result = set
                    .Where(h => h.Time >= start && h.Time < end)
                    .OrderBy(h => h.Time)
                    .ToList();

                return await Task.FromResult(result);
            });

            EnergyHistory? previous = null;

            foreach (var history in histories)
            {
                if (previous != null)
                {
                    var gridPower = new GridPower(history, previous);
                    var isConsumer = gridPower.IsConsumer;
                    var price = await _calculationService.CalculateEnergyPrice(history.Time, isConsumer);
                    var cost = (decimal)(gridPower.TotalInversed * (price ?? 0.0) / 1000.0);

                    AddResult(monthResults, previous.Time, gridPower.TotalConsumed,
                        gridPower.TotalProduced, (decimal)(price ?? 0.0), cost);
                }

                previous = history;
            }

            // ── Source 2: QuarterlyMeasurements ──────────────────────────────
            var measurements = await _measurementService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= start && m.Time < end)
                    .OrderBy(m => m.Time)
                    .ToList();

                return await Task.FromResult(result);
            });

            foreach (var m in measurements)
            {
                double netWh = m.GridImportWh - m.GridExportWh;
                bool isConsumer = netWh >= 0;
                var price = await _calculationService.CalculateEnergyPrice(m.Time, isConsumer);
                var cost = (decimal)(netWh * (price ?? 0.0) / 1000.0);

                AddResult(monthResults, m.Time, m.GridImportWh, m.GridExportWh,
                    (decimal)(price ?? 0.0), cost);
            }

            // Sort each month's results by time.
            foreach (var mr in monthResults)
                mr.FinancialResultsList = mr.FinancialResultsList.OrderBy(r => r.Time).ToList();

            return monthResults
                .OrderBy(mr => mr.Year)
                .ThenBy(mr => mr.Month)
                .AsQueryable();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void AddResult(
            List<FinancialMonthResult> monthResults,
            DateTime time,
            double consumed,
            double produced,
            decimal price,
            decimal cost)
        {
            var monthResult = monthResults
                .FirstOrDefault(mr => mr.Year == time.Year && mr.Month == time.Month);

            if (monthResult == null)
            {
                monthResult = new FinancialMonthResult { Year = time.Year, Month = time.Month };
                monthResults.Add(monthResult);
            }

            monthResult.FinancialResultsList.Add(new FinancialResult
            {
                Time = time,
                Consumed = consumed,
                Produced = produced,
                Price = price,
                Cost = cost
            });
        }
    }
}