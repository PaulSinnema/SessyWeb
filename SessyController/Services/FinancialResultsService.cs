using SessyController.Services.Items;
using SessyCommon.Extensions;
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

                    // Consumption and feed-in are priced separately and then netted.
                    // This is correct both with netting (buy ≈ sell, so it equals the net)
                    // and without netting (sell << buy), where they must not be salded.
                    var buyPrice = await _calculationService.CalculateEnergyPrice(history.Time, true);
                    var sellPrice = await _calculationService.CalculateEnergyPrice(history.Time, false);

                    double consumedKWh = gridPower.TotalConsumed / 1000.0;
                    double producedKWh = gridPower.TotalProduced / 1000.0;

                    // Sign convention (matches GridPower.TotalInversed): positive = you pay,
                    // negative = you receive. Consumption costs money, feed-in returns it.
                    var cost = (decimal)(consumedKWh * (buyPrice ?? 0.0)
                                       - producedKWh * (sellPrice ?? 0.0));

                    // Report the buy price as the headline price for the quarter.
                    AddResult(monthResults, previous.Time, gridPower.TotalConsumed,
                        gridPower.TotalProduced, (decimal)(buyPrice ?? 0.0), cost);
                }

                previous = history;
            }

            // ── Source 2: QuarterlyMeasurements ──────────────────────────────
            // Only for quarters NOT covered by EnergyHistory. Now that meter readings are
            // stored again, the same quarter could appear in both sources; this guard
            // prevents counting the grid flow twice.
            var historyTimes = new HashSet<DateTime>(
                histories.Select(h => h.Time.DateFloorQuarter()));

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
                if (historyTimes.Contains(m.Time.DateFloorQuarter()))
                    continue;

                // Import and export are priced separately and netted, same as Source 1.
                var buyPrice = await _calculationService.CalculateEnergyPrice(m.Time, true);
                var sellPrice = await _calculationService.CalculateEnergyPrice(m.Time, false);

                double importKWh = m.GridImportWh / 1000.0;
                double exportKWh = m.GridExportWh / 1000.0;

                // Sign convention: positive = you pay, negative = you receive.
                var cost = (decimal)(importKWh * (buyPrice ?? 0.0)
                                   - exportKWh * (sellPrice ?? 0.0));

                AddResult(monthResults, m.Time, m.GridImportWh, m.GridExportWh,
                    (decimal)(buyPrice ?? 0.0), cost);
            }

            // Sort each month's results by time.
            foreach (var mr in monthResults)
                mr.FinancialResultsList = mr.FinancialResultsList!.OrderBy(r => r.Time).ToList();

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