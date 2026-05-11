using SessyCommon.Services;
using SessyController.Services.Statistics;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Calculates energy and financial statistics for a given period.
    /// Uses EnergyHistory (P1 meter data), Performance (battery/solar data)
    /// and Investment (purchase costs) as data sources.
    /// </summary>
    public class EnergyStatisticsService
    {
        private readonly EnergyHistoryDataService _energyHistoryDataService;
        private readonly PerformanceDataService _performanceDataService;
        private readonly InvestmentDataService _investmentDataService;
        private readonly TimeZoneService _timeZoneService;

        // Assumed battery capacity in kWh for cycle calculation.
        // Could be made configurable if needed.
        private const double BatteryCapacityKWh = 16.2;

        // kWp of the solar installation — used for performance ratio.
        // Could be made configurable.
        private const double SolarInstallationKWp = 5.54;

        public EnergyStatisticsService(EnergyHistoryDataService energyHistoryDataService,
                                       PerformanceDataService performanceDataService,
                                       InvestmentDataService investmentDataService,
                                       TimeZoneService timeZoneService)
        {
            _energyHistoryDataService = energyHistoryDataService;
            _performanceDataService = performanceDataService;
            _investmentDataService = investmentDataService;
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// Calculates comprehensive energy statistics for the given period.
        /// </summary>
        public async Task<EnergyStatistics> GetEnergyStatisticsAsync(DateTime start, DateTime end)
        {
            var energyHistory = await GetEnergyHistoryAsync(start, end);
            var performance = await GetPerformanceAsync(start, end);

            var stats = new EnergyStatistics
            {
                PeriodStart = start,
                PeriodEnd = end
            };

            CalculateGridFlows(energyHistory, stats);
            CalculateSolarStats(performance, stats);
            CalculateBatteryStats(performance, stats);
            CalculateFinancialStats(performance, stats);
            CalculateConsumptionStats(performance, energyHistory, stats);

            return stats;
        }

        /// <summary>
        /// Calculates monthly trends for the given period.
        /// </summary>
        public async Task<List<MonthlyTrend>> GetMonthlyTrendsAsync(DateTime start, DateTime end)
        {
            var trends = new List<MonthlyTrend>();

            var current = new DateTime(start.Year, start.Month, 1);

            while (current <= end)
            {
                var monthEnd = current.AddMonths(1).AddTicks(-1);
                var clampedEnd = monthEnd > end ? end : monthEnd;

                var stats = await GetEnergyStatisticsAsync(current, clampedEnd);

                trends.Add(new MonthlyTrend
                {
                    Year = current.Year,
                    Month = current.Month,
                    SolarProductionKWh = stats.TotalSolarProductionKWh,
                    ConsumptionKWh = stats.TotalConsumptionKWh,
                    GridImportKWh = stats.TotalGridImportKWh,
                    GridExportKWh = stats.TotalGridExportKWh,
                    SavingsEur = stats.TotalSavingsEur,
                    ArbitrageProfitEur = stats.ArbitrageProfitEur,
                    SelfSufficiencyPct = stats.SelfSufficiencyPct,
                    BatteryCycles = stats.BatteryCycles
                });

                current = current.AddMonths(1);
            }

            return trends;
        }

        /// <summary>
        /// Calculates ROI and payback period statistics.
        /// Uses the last 90 days of energy data to project future savings.
        /// </summary>
        public async Task<InvestmentStatistics> GetInvestmentStatisticsAsync()
        {
            var investments = await _investmentDataService.GetList(async set =>
            {
                return await Task.FromResult(set.ToList());
            });

            if (!investments.Any())
            {
                return new InvestmentStatistics
                {
                    PeriodStart = _timeZoneService.Now,
                    PeriodEnd = _timeZoneService.Now
                };
            }

            var earliestPurchase = investments.Min(i => i.PurchaseDate);
            var now = _timeZoneService.Now;

            // Calculate total savings since earliest purchase.
            var lifetimeStats = await GetEnergyStatisticsAsync(earliestPurchase, now);

            // Use last 90 days to project annual savings.
            var recentStart = now.AddDays(-90);
            var recentStats = await GetEnergyStatisticsAsync(recentStart, now);

            double totalNetInvestment = investments.Sum(i => i.NetAmountEur);
            double projectedAnnualSavings = recentStats.AnnualSavingsEur;

            DateTime? breakEvenDate = null;

            if (projectedAnnualSavings > 0)
            {
                double remainingToRecover = totalNetInvestment - lifetimeStats.TotalSavingsEur;

                if (remainingToRecover <= 0)
                {
                    breakEvenDate = now; // Already recovered!
                }
                else
                {
                    double yearsToBreakEven = remainingToRecover / projectedAnnualSavings;
                    breakEvenDate = now.AddDays(yearsToBreakEven * 365.0);
                }
            }

            // Build category breakdown.
            var categoryBreakdown = investments
                .GroupBy(i => i.Category)
                .Select(g => new InvestmentCategoryStats
                {
                    Category = g.Key,
                    TotalAmountEur = g.Sum(i => i.AmountEur),
                    TotalSubsidyEur = g.Sum(i => i.SubsidyEur),
                    ExpectedLifetimeYears = (int)g.Average(i => i.ExpectedLifetimeYears)
                })
                .OrderByDescending(c => c.NetAmountEur)
                .ToList();

            return new InvestmentStatistics
            {
                PeriodStart = earliestPurchase,
                PeriodEnd = now,
                TotalNetInvestmentEur = totalNetInvestment,
                TotalRealizedSavingsEur = lifetimeStats.TotalSavingsEur,
                ProjectedAnnualSavingsEur = projectedAnnualSavings,
                ProjectedBreakEvenDate = breakEvenDate,
                CategoryBreakdown = categoryBreakdown
            };
        }

        // ── Private calculation methods ──────────────────────────────────────

        private void CalculateGridFlows(List<EnergyHistory> history, EnergyStatistics stats)
        {
            if (history.Count < 2)
                return;

            var ordered = history.OrderBy(h => h.Time).ToList();
            var first = ordered.First();
            var last = ordered.Last();

            // Grid import = consumed tariff delta.
            stats.TotalGridImportKWh =
                (last.ConsumedTariff1 - first.ConsumedTariff1) +
                (last.ConsumedTariff2 - first.ConsumedTariff2);

            // Grid export = produced tariff delta.
            stats.TotalGridExportKWh =
                (last.ProducedTariff1 - first.ProducedTariff1) +
                (last.ProducedTariff2 - first.ProducedTariff2);

            // Clamp to zero to avoid negative values from meter resets.
            stats.TotalGridImportKWh = Math.Max(0, stats.TotalGridImportKWh);
            stats.TotalGridExportKWh = Math.Max(0, stats.TotalGridExportKWh);
        }

        private void CalculateSolarStats(List<Performance> performance, EnergyStatistics stats)
        {
            if (!performance.Any())
                return;

            // Solar production per quarter = SolarPowerPerQuarterHour kWh.
            stats.TotalSolarProductionKWh = performance
                .Sum(p => p.SolarPowerPerQuarterHour);

            // Peak daily solar production.
            stats.PeakDailySolarProductionKWh = performance
                .GroupBy(p => p.Time.Date)
                .Select(g => g.Sum(p => p.SolarPowerPerQuarterHour))
                .DefaultIfEmpty(0)
                .Max();

            // Self-consumed solar = production - export.
            stats.SelfConsumedSolarKWh = Math.Max(0,
                stats.TotalSolarProductionKWh - stats.TotalGridExportKWh);

            // Solar performance ratio: actual production vs theoretical maximum.
            // Theoretical = peak hours * kWp (simplified).
            double totalRadiation = performance.Sum(p => p.SolarGlobalRadiation);
            double theoreticalProductionKWh = totalRadiation / 1000.0 * SolarInstallationKWp;

            stats.SolarPerformanceRatio = theoreticalProductionKWh > 0
                ? stats.TotalSolarProductionKWh / theoreticalProductionKWh
                : 0.0;
        }

        private void CalculateBatteryStats(List<Performance> performance, EnergyStatistics stats)
        {
            if (!performance.Any())
                return;

            // Charged = sum of all charging quarters (kWh per quarter = W * 0.25h / 1000).
            stats.TotalBatteryChargedKWh = performance
                .Where(p => p.Charging)
                .Sum(p => p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            // Discharged = sum of all discharging quarters.
            stats.TotalBatteryDischargedKWh = performance
                .Where(p => p.Discharging)
                .Sum(p => p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            // Battery cycles = total charged energy / battery capacity.
            stats.BatteryCycles = BatteryCapacityKWh > 0
                ? stats.TotalBatteryChargedKWh / BatteryCapacityKWh
                : 0.0;

            // Average SOC.
            stats.AverageSocPct = performance.Any()
                ? performance.Average(p => p.ChargeLeftPercentage)
                : 0.0;
        }

        private void CalculateFinancialStats(List<Performance> performance, EnergyStatistics stats)
        {
            if (!performance.Any())
                return;

            // Actual cost = import cost - export revenue.
            double importCost = performance
                .Where(p => !p.Charging && !p.Discharging)
                .Sum(p => p.BuyingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            double chargingCost = performance
                .Where(p => p.Charging)
                .Sum(p => p.BuyingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            stats.GridExportRevenueEur = performance
                .Where(p => p.Discharging)
                .Sum(p => p.SellingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            stats.ActualEnergyCostEur = importCost + chargingCost - stats.GridExportRevenueEur;

            // Arbitrage profit = discharging revenue - charging cost.
            stats.ArbitrageProfitEur = stats.GridExportRevenueEur - chargingCost;

            // Baseline cost = same consumption at average buying price (no solar/battery).
            double avgBuyPrice = performance.Any()
                ? performance.Average(p => p.BuyingPrice)
                : 0.0;

            stats.BaselineEnergyCostEur = stats.TotalConsumptionKWh * avgBuyPrice;

            // Weighted average prices.
            double totalImportKWh = stats.TotalGridImportKWh;
            double totalExportKWh = stats.TotalGridExportKWh;

            stats.WeightedAvgBuyPriceEurPerKWh = totalImportKWh > 0
                ? importCost / totalImportKWh
                : avgBuyPrice;

            stats.WeightedAvgSellPriceEurPerKWh = totalExportKWh > 0
                ? stats.GridExportRevenueEur / totalExportKWh
                : 0.0;
        }

        private void CalculateConsumptionStats(
            List<Performance> performance,
            List<EnergyHistory> history,
            EnergyStatistics stats)
        {
            if (!performance.Any())
                return;

            // Total consumption = solar + import - export (energy balance).
            stats.TotalConsumptionKWh = Math.Max(0,
                stats.TotalSolarProductionKWh +
                stats.TotalGridImportKWh -
                stats.TotalGridExportKWh);

            // Peak daily consumption from P1 history.
            if (history.Count >= 2)
            {
                var ordered = history.OrderBy(h => h.Time).ToList();

                stats.PeakDailyConsumptionKWh = ordered
                    .Zip(ordered.Skip(1), (a, b) => new
                    {
                        Date = a.Time.Date,
                        Consumed =
                            (b.ConsumedTariff1 - a.ConsumedTariff1) +
                            (b.ConsumedTariff2 - a.ConsumedTariff2)
                    })
                    .GroupBy(x => x.Date)
                    .Select(g => g.Sum(x => x.Consumed))
                    .DefaultIfEmpty(0)
                    .Max();
            }

            // Weekday vs weekend consumption.
            stats.WeekdayConsumptionKWh = performance
                .Where(p => p.Time.DayOfWeek != DayOfWeek.Saturday &&
                            p.Time.DayOfWeek != DayOfWeek.Sunday)
                .Sum(p => p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            stats.WeekendConsumptionKWh = performance
                .Where(p => p.Time.DayOfWeek == DayOfWeek.Saturday ||
                            p.Time.DayOfWeek == DayOfWeek.Sunday)
                .Sum(p => p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);
        }

        // ── Data access helpers ──────────────────────────────────────────────

        private async Task<List<EnergyHistory>> GetEnergyHistoryAsync(DateTime start, DateTime end)
        {
            return await _energyHistoryDataService.GetList(async set =>
            {
                var result = set
                    .Where(h => h.Time >= start && h.Time <= end)
                    .OrderBy(h => h.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }

        private async Task<List<Performance>> GetPerformanceAsync(DateTime start, DateTime end)
        {
            return await _performanceDataService.GetList(async set =>
            {
                var result = set
                    .Where(p => p.Time >= start && p.Time <= end)
                    .OrderBy(p => p.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }
    }
}