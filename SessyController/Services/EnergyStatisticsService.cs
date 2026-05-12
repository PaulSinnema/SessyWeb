using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
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
    ///
    /// Unit conventions:
    ///   EnergyHistory.ConsumedTariff1/2  — P1 meter stands in Wh  → divide by 1000 for kWh
    ///   EnergyHistory.ProducedTariff1/2  — P1 meter stands in Wh  → divide by 1000 for kWh
    ///   Performance.SolarPowerPerQuarterHour — kWh per quarter-hour (already kWh)
    ///   Performance.EstimatedConsumptionPerQuarterHour — Watts (average power) → * 0.25 / 1000 for kWh
    ///   Performance.ChargeLeft           — Wh
    ///   Performance.ChargeLeftPercentage — % (0-100)
    /// </summary>
    public class EnergyStatisticsService
    {
        private readonly EnergyHistoryDataService _energyHistoryDataService;
        private readonly PerformanceDataService _performanceDataService;
        private readonly InvestmentDataService _investmentDataService;
        private readonly TimeZoneService _timeZoneService;
        private readonly HeatPumpConfig _heatPumpConfig;

        // Total battery capacity in kWh for cycle calculation (3x Sessy 5.4 kWh).
        private const double BatteryCapacityKWh = 16.2;

        // Solar installation peak power in kWp — used for performance ratio.
        private const double SolarInstallationKWp = 5.54;

        // P1 meter tand unit: Wh → kWh conversion factor.
        private const double WhToKWh = 1000.0;

        // Category name constants for per-component savings routing.
        // Matching is case-insensitive — see NormalizeCategory().
        private const string CategorySolar = "solarpanels";
        private const string CategoryBattery = "battery";
        private const string CategoryHeatPump = "heatpump";

        /// <summary>
        /// Normalizes a category name for matching — lowercase, no spaces or hyphens.
        /// Supports common aliases: "solar", "zonnepanelen", "batterij", "warmtepomp" etc.
        /// </summary>
        private static string NormalizeCategory(string category)
        {
            var normalized = category.ToLowerInvariant()
                .Replace(" ", "")
                .Replace("-", "")
                .Replace("_", "");

            // Common aliases.
            return normalized switch
            {
                "solar" or "zonnepanelen" or "panelen" => CategorySolar,
                "batterij" or "batteries" or "sessy" => CategoryBattery,
                "warmtepomp" or "heatpump" or "hp" or "daikin" => CategoryHeatPump,
                _ => normalized
            };
        }

        public EnergyStatisticsService(EnergyHistoryDataService energyHistoryDataService,
                                       PerformanceDataService performanceDataService,
                                       InvestmentDataService investmentDataService,
                                       TimeZoneService timeZoneService,
                                       IOptions<HeatPumpConfig> heatPumpConfig)
        {
            _energyHistoryDataService = energyHistoryDataService;
            _performanceDataService = performanceDataService;
            _investmentDataService = investmentDataService;
            _timeZoneService = timeZoneService;
            _heatPumpConfig = heatPumpConfig.Value;
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

            // Order matters: consumption depends on grid flows and solar.
            CalculateGridFlows(energyHistory, stats);
            CalculateSolarStats(performance, stats);
            CalculateConsumptionStats(performance, energyHistory, stats);
            CalculateBatteryStats(performance, stats);
            CalculateFinancialStats(performance, stats);

            return stats;
        }

        /// <summary>
        /// Calculates monthly trends for the given period.
        /// </summary>
        public async Task<List<MonthlyTrend>> GetMonthlyTrendsAsync(DateTime start, DateTime end)
        {
            var trends = new List<MonthlyTrend>();

            // Clamp extreme values to avoid DateTime overflow in AddMonths/AddTicks.
            var clampedStart = start == DateTime.MinValue ? new DateTime(2020, 1, 1) : start;
            var clampedEnd = end == DateTime.MaxValue ? _timeZoneService.Now : end;

            var current = new DateTime(clampedStart.Year, clampedStart.Month, 1);

            while (current <= clampedEnd)
            {
                var monthEnd = current.AddMonths(1).AddTicks(-1);
                var periodEnd = monthEnd > clampedEnd ? clampedEnd : monthEnd;

                var stats = await GetEnergyStatisticsAsync(current, periodEnd);

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
        /// Calculates ROI and payback period statistics per investment component.
        /// - Solar panels: based on measured export revenue + self-consumption value
        /// - Batteries: based on measured arbitrage profit
        /// - Heat pump: based on HeatPumpConfig (gas savings)
        /// - Other: uses EstimatedAnnualSavingsEur from Investment record
        /// ROI is always calculated from PurchaseDate regardless of selected period.
        /// When historical data is incomplete, uses seasonal extrapolation.
        /// </summary>
        public async Task<InvestmentStatistics> GetInvestmentStatisticsAsync()
        {
            var investments = await _investmentDataService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            if (!investments.Any())
            {
                return new InvestmentStatistics
                {
                    PeriodStart = _timeZoneService.Now,
                    PeriodEnd = _timeZoneService.Now
                };
            }

            var now = _timeZoneService.Now;
            var earliestPurchase = investments.Min(i => i.PurchaseDate);

            // ── Get data availability range ───────────────────────────────────
            var dataStart = await GetEarliestDataDateAsync();

            // ── Build seasonal monthly averages from available data ────────────
            var monthlySolarSavings = await BuildSeasonalSolarSavingsAsync(dataStart, now);
            var monthlyArbitrageSavings = await BuildSeasonalArbitrageSavingsAsync(dataStart, now);

            // ── Build per-category stats ──────────────────────────────────────
            var categoryBreakdown = investments
                .GroupBy(i => i.Category)
                .Select(g =>
                {
                    var installDate = g.Min(i => i.PurchaseDate);
                    var monthsSince = (now.Year - installDate.Year) * 12 +
                                      (now.Month - installDate.Month);

                    double annualSavings = CalculateCategoryAnnualSavings(
                        g.Key,
                        g.First(),
                        monthlySolarSavings,
                        monthlyArbitrageSavings);

                    string savingsSource = GetSavingsSource(g.Key, g.First());

                    return new InvestmentCategoryStats
                    {
                        Category = g.Key,
                        TotalAmountEur = g.Sum(i => i.AmountEur),
                        TotalSubsidyEur = g.Sum(i => i.SubsidyEur),
                        ExpectedLifetimeYears = (int)g.Average(i => i.ExpectedLifetimeYears),
                        InstallationDate = installDate,
                        MonthsSinceInstallation = monthsSince,
                        AnnualSavingsEur = annualSavings,
                        SavingsSource = savingsSource
                    };
                })
                .OrderByDescending(c => c.NetAmountEur)
                .ToList();

            // ── Totals ────────────────────────────────────────────────────────
            double totalNetInvestment = categoryBreakdown.Sum(c => c.NetAmountEur);
            double totalProjectedSavings = categoryBreakdown.Sum(c => c.ProjectedTotalSavingsEur);
            double totalAnnualSavings = categoryBreakdown.Sum(c => c.AnnualSavingsEur);

            // Realized savings = only from actual measured data period.
            var realizedStats = await GetEnergyStatisticsAsync(dataStart, now);
            double realizedSavings = realizedStats.TotalSavingsEur +
                                     (realizedStats.ArbitrageProfitEur > 0 ? realizedStats.ArbitrageProfitEur : 0) +
                                     CalculateHeatPumpSavings(dataStart, now);

            // ── Break-even date ───────────────────────────────────────────────
            DateTime? breakEvenDate = null;

            if (totalAnnualSavings > 0)
            {
                double remaining = totalNetInvestment - totalProjectedSavings;
                breakEvenDate = remaining <= 0
                    ? now
                    : now.AddDays(remaining / totalAnnualSavings * 365.0);
            }

            return new InvestmentStatistics
            {
                PeriodStart = earliestPurchase,
                PeriodEnd = now,
                DataAvailableFrom = dataStart,
                TotalNetInvestmentEur = totalNetInvestment,
                TotalRealizedSavingsEur = realizedSavings,
                ProjectedTotalSavingsEur = totalProjectedSavings,
                ProjectedAnnualSavingsEur = totalAnnualSavings,
                ProjectedBreakEvenDate = breakEvenDate,
                CategoryBreakdown = categoryBreakdown
            };
        }

        /// <summary>
        /// Calculates annual savings for a specific investment category.
        /// </summary>
        private double CalculateCategoryAnnualSavings(
            string category,
            Investment representative,
            Dictionary<int, double> monthlySolarSavings,
            Dictionary<int, double> monthlyArbitrageSavings)
        {
            // Manual override takes precedence.
            if (representative.EstimatedAnnualSavingsEur > 0)
                return representative.EstimatedAnnualSavingsEur;

            return NormalizeCategory(category) switch
            {
                CategorySolar => monthlySolarSavings.Values.Sum(),
                CategoryBattery => monthlyArbitrageSavings.Values.Sum(),
                CategoryHeatPump => _heatPumpConfig.IsConfigured
                    ? _heatPumpConfig.TotalAnnualSavingsEur
                    : 0.0,
                _ => 0.0
            };
        }

        private string GetSavingsSource(string category, Investment representative)
        {
            if (representative.EstimatedAnnualSavingsEur > 0)
                return representative.SavingsDescription ?? "Manual estimate";

            return NormalizeCategory(category) switch
            {
                CategorySolar => "Export revenue + self-consumption (measured)",
                CategoryBattery => "Arbitrage profit (measured)",
                CategoryHeatPump => _heatPumpConfig.IsConfigured
                    ? $"{_heatPumpConfig.AnnualGasConsumptionM3} m³ × €{_heatPumpConfig.GasPriceEurPerM3}/m³ + €{_heatPumpConfig.GasStandingChargeEurPerYear} standing charge"
                    : "Not configured — add HeatPumpConfig to appsettings.json",
                _ => "Manual estimate required — set EstimatedAnnualSavingsEur on investment"
            };
        }

        /// <summary>
        /// Calculates heat pump savings for a specific period.
        /// </summary>
        private double CalculateHeatPumpSavings(DateTime start, DateTime end)
        {
            if (!_heatPumpConfig.IsConfigured)
                return 0.0;

            var effectiveStart = start > _heatPumpConfig.InstallationDate
                ? start
                : _heatPumpConfig.InstallationDate;

            if (effectiveStart >= end)
                return 0.0;

            double months = (end.Year - effectiveStart.Year) * 12 +
                            (end.Month - effectiveStart.Month);

            return _heatPumpConfig.MonthlyAverageSavingsEur * months;
        }

        /// <summary>
        /// Builds a dictionary of average monthly solar savings (export revenue + self-consumption)
        /// per calendar month (1-12) from available Performance data.
        /// </summary>
        private async Task<Dictionary<int, double>> BuildSeasonalSolarSavingsAsync(
            DateTime dataStart, DateTime dataEnd)
        {
            var allMonthly = new Dictionary<int, List<double>>();

            var current = new DateTime(dataStart.Year, dataStart.Month, 1);

            while (current <= dataEnd)
            {
                var monthEnd = current.AddMonths(1).AddTicks(-1);
                var clampedEnd = monthEnd > dataEnd ? dataEnd : monthEnd;

                var perf = await GetPerformanceAsync(current, clampedEnd);

                // Solar savings = export revenue + self-consumed solar value.
                double exportRevenue = perf
                    .Where(p => p.Discharging)
                    .Sum(p => p.SellingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

                double solarKWh = perf.Sum(p => p.SolarPowerPerQuarterHour);
                double avgBuyPrice = perf.Any() ? perf.Average(p => p.BuyingPrice) : 0.0;
                double selfConsumedValue = solarKWh * avgBuyPrice;

                double monthlySaving = exportRevenue + selfConsumedValue;

                int month = current.Month;
                if (!allMonthly.ContainsKey(month))
                    allMonthly[month] = new List<double>();

                allMonthly[month].Add(monthlySaving);
                current = current.AddMonths(1);
            }

            // Average per calendar month, fill missing with overall average.
            var result = new Dictionary<int, double>();
            double overallAvg = allMonthly.Values.SelectMany(v => v).DefaultIfEmpty(0).Average();

            for (int m = 1; m <= 12; m++)
                result[m] = allMonthly.ContainsKey(m) ? allMonthly[m].Average() : overallAvg;

            return result;
        }

        /// <summary>
        /// Builds a dictionary of average monthly arbitrage profit
        /// per calendar month (1-12) from available Performance data.
        /// </summary>
        private async Task<Dictionary<int, double>> BuildSeasonalArbitrageSavingsAsync(
            DateTime dataStart, DateTime dataEnd)
        {
            var allMonthly = new Dictionary<int, List<double>>();

            var current = new DateTime(dataStart.Year, dataStart.Month, 1);

            while (current <= dataEnd)
            {
                var monthEnd = current.AddMonths(1).AddTicks(-1);
                var clampedEnd = monthEnd > dataEnd ? dataEnd : monthEnd;

                var perf = await GetPerformanceAsync(current, clampedEnd);

                double dischargingRevenue = perf
                    .Where(p => p.Discharging)
                    .Sum(p => p.SellingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

                double chargingCost = perf
                    .Where(p => p.Charging)
                    .Sum(p => p.BuyingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

                double arbitrage = dischargingRevenue - chargingCost;

                int month = current.Month;
                if (!allMonthly.ContainsKey(month))
                    allMonthly[month] = new List<double>();

                allMonthly[month].Add(arbitrage);
                current = current.AddMonths(1);
            }

            var result = new Dictionary<int, double>();
            double overallAvg = allMonthly.Values.SelectMany(v => v).DefaultIfEmpty(0).Average();

            for (int m = 1; m <= 12; m++)
                result[m] = allMonthly.ContainsKey(m) ? allMonthly[m].Average() : overallAvg;

            return result;
        }

        /// <summary>
        /// Returns the earliest date for which we have Performance data.
        /// Falls back to 1 year ago if no data is available.
        /// </summary>
        private async Task<DateTime> GetEarliestDataDateAsync()
        {
            var earliest = await _performanceDataService.Get(async set =>
            {
                var result = set.OrderBy(p => p.Time).FirstOrDefault();
                return await Task.FromResult(result);
            });

            return earliest?.Time ?? _timeZoneService.Now.AddYears(-1);
        }

        // ── Private calculation methods ──────────────────────────────────────

        /// <summary>
        /// Calculates grid import and export from P1 meter tand deltas.
        /// P1 meter tands are stored in Wh → divide by 1000 for kWh.
        /// </summary>
        private void CalculateGridFlows(List<EnergyHistory> history, EnergyStatistics stats)
        {
            if (history.Count < 2)
                return;

            var ordered = history.OrderBy(h => h.Time).ToList();
            var first = ordered.First();
            var last = ordered.Last();

            // Delta of cumulative P1 meter tands (Wh) → kWh.
            stats.TotalGridImportKWh =
                ((last.ConsumedTariff1 - first.ConsumedTariff1) +
                 (last.ConsumedTariff2 - first.ConsumedTariff2)) / WhToKWh;

            stats.TotalGridExportKWh =
                ((last.ProducedTariff1 - first.ProducedTariff1) +
                 (last.ProducedTariff2 - first.ProducedTariff2)) / WhToKWh;

            // Clamp to zero — negative deltas indicate meter resets.
            stats.TotalGridImportKWh = Math.Max(0, stats.TotalGridImportKWh);
            stats.TotalGridExportKWh = Math.Max(0, stats.TotalGridExportKWh);
        }

        /// <summary>
        /// Calculates solar production statistics from Performance records.
        /// SolarPowerPerQuarterHour is already in kWh per quarter-hour.
        /// </summary>
        private void CalculateSolarStats(List<Performance> performance, EnergyStatistics stats)
        {
            if (!performance.Any())
                return;

            // SolarPowerPerQuarterHour is kWh per quarter → sum directly.
            stats.TotalSolarProductionKWh = performance
                .Sum(p => p.SolarPowerPerQuarterHour);

            stats.PeakDailySolarProductionKWh = performance
                .GroupBy(p => p.Time.Date)
                .Select(g => g.Sum(p => p.SolarPowerPerQuarterHour))
                .DefaultIfEmpty(0)
                .Max();

            // Self-consumed solar = production - export (what did not go to the grid).
            stats.SelfConsumedSolarKWh = Math.Max(0,
                stats.TotalSolarProductionKWh - stats.TotalGridExportKWh);

            // Solar performance ratio: actual vs theoretical based on global radiation.
            // Theoretical = radiation (W/m²) integrated over time * system kWp / 1000.
            // Each quarter-hour radiation value is averaged W/m² → integrate: * (15min / 60) hours.
            double theoreticalProductionKWh = performance
                .Sum(p => p.SolarGlobalRadiation / 1000.0 * SolarInstallationKWp * 0.25);

            stats.SolarPerformanceRatio = theoreticalProductionKWh > 0
                ? stats.TotalSolarProductionKWh / theoreticalProductionKWh
                : 0.0;
        }

        /// <summary>
        /// Calculates total consumption and weekday/weekend split.
        /// Total consumption = grid import + solar production - grid export (energy balance).
        /// Weekday/weekend split uses P1 meter deltas between consecutive readings.
        /// </summary>
        private void CalculateConsumptionStats(
            List<Performance> performance,
            List<EnergyHistory> history,
            EnergyStatistics stats)
        {
            // Total consumption via energy balance (kWh).
            stats.TotalConsumptionKWh = Math.Max(0,
                stats.TotalGridImportKWh +
                stats.TotalSolarProductionKWh -
                stats.TotalGridExportKWh);

            // Peak daily consumption from P1 meter deltas.
            if (history.Count >= 2)
            {
                var ordered = history.OrderBy(h => h.Time).ToList();

                stats.PeakDailyConsumptionKWh = ordered
                    .Zip(ordered.Skip(1), (a, b) => new
                    {
                        Date = a.Time.Date,
                        // Delta of P1 tands (Wh) → kWh.
                        ConsumedKWh =
                            ((b.ConsumedTariff1 - a.ConsumedTariff1) +
                             (b.ConsumedTariff2 - a.ConsumedTariff2)) / WhToKWh
                    })
                    .Where(x => x.ConsumedKWh >= 0) // guard against meter resets
                    .GroupBy(x => x.Date)
                    .Select(g => g.Sum(x => x.ConsumedKWh))
                    .DefaultIfEmpty(0)
                    .Max();
            }

            // Weekday vs weekend from Performance estimated consumption (W → kWh per quarter).
            stats.WeekdayConsumptionKWh = performance
                .Where(p => p.Time.DayOfWeek != DayOfWeek.Saturday &&
                            p.Time.DayOfWeek != DayOfWeek.Sunday)
                .Sum(p => p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            stats.WeekendConsumptionKWh = performance
                .Where(p => p.Time.DayOfWeek == DayOfWeek.Saturday ||
                            p.Time.DayOfWeek == DayOfWeek.Sunday)
                .Sum(p => p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);
        }

        /// <summary>
        /// Calculates battery charge/discharge energy from ChargeLeft deltas.
        /// ChargeLeft is stored in Wh → divide by 1000 for kWh.
        /// </summary>
        private void CalculateBatteryStats(List<Performance> performance, EnergyStatistics stats)
        {
            if (!performance.Any())
                return;

            // Sum energy throughput per charging/discharging quarter.
            // During Charging: delta ChargeLeft between consecutive quarters = energy stored (Wh → kWh).
            // During Discharging: delta ChargeLeft = energy released (Wh → kWh).
            var ordered = performance.OrderBy(p => p.Time).ToList();

            double totalChargedKWh = 0.0;
            double totalDischargedKWh = 0.0;

            for (int i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var curr = ordered[i];

                double deltaWh = curr.ChargeLeft - prev.ChargeLeft;

                if (curr.Charging && deltaWh > 0)
                    totalChargedKWh += deltaWh / 1000.0;
                else if (curr.Discharging && deltaWh < 0)
                    totalDischargedKWh += Math.Abs(deltaWh) / 1000.0;
            }

            stats.TotalBatteryChargedKWh = totalChargedKWh;
            stats.TotalBatteryDischargedKWh = totalDischargedKWh;

            // Battery cycles = total charged energy / full battery capacity.
            stats.BatteryCycles = BatteryCapacityKWh > 0
                ? stats.TotalBatteryChargedKWh / BatteryCapacityKWh
                : 0.0;

            // Round-trip efficiency only meaningful when both charged and discharged > 0.1 kWh.
            // Below that threshold the delta method is too noisy.
            stats.AverageSocPct = performance.Average(p => p.ChargeLeftPercentage);
        }

        /// <summary>
        /// Calculates financial statistics.
        /// Actual cost = what was actually paid for grid import + charging minus export revenue.
        /// Baseline = what would have been paid without solar/battery (same consumption at avg price).
        /// </summary>
        private void CalculateFinancialStats(List<Performance> performance, EnergyStatistics stats)
        {
            if (!performance.Any())
                return;

            // Charging cost: energy bought from grid to charge batteries (W → kWh per quarter).
            double chargingCostEur = performance
                .Where(p => p.Charging)
                .Sum(p => p.BuyingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            // ZeroNetHome/Disabled cost: energy bought for household consumption (W → kWh per quarter).
            double importCostEur = performance
                .Where(p => p.ZeroNetHome || p.Disabled)
                .Sum(p => p.BuyingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            // Export revenue: energy sold while discharging (W → kWh per quarter).
            stats.GridExportRevenueEur = performance
                .Where(p => p.Discharging)
                .Sum(p => p.SellingPrice * p.EstimatedConsumptionPerQuarterHour * 0.25 / 1000.0);

            stats.ActualEnergyCostEur = importCostEur + chargingCostEur - stats.GridExportRevenueEur;

            // Arbitrage profit = discharging revenue - charging cost.
            stats.ArbitrageProfitEur = stats.GridExportRevenueEur - chargingCostEur;

            // Baseline cost: same total consumption at average grid buying price (no solar/battery).
            // Weighted average buy price per kWh — weighted by consumption per quarter.
            double totalConsumptionW = performance
                .Sum(p => p.EstimatedConsumptionPerQuarterHour);

            stats.WeightedAvgBuyPriceEurPerKWh = totalConsumptionW > 0
                ? performance.Sum(p => p.BuyingPrice * p.EstimatedConsumptionPerQuarterHour) / totalConsumptionW
                : performance.Average(p => p.BuyingPrice);

            // Weighted average sell price — weighted by consumption during discharging quarters.
            double totalDischargingW = performance
                .Where(p => p.Discharging)
                .Sum(p => p.EstimatedConsumptionPerQuarterHour);

            stats.WeightedAvgSellPriceEurPerKWh = totalDischargingW > 0
                ? performance
                    .Where(p => p.Discharging)
                    .Sum(p => p.SellingPrice * p.EstimatedConsumptionPerQuarterHour) / totalDischargingW
                : performance.Average(p => p.SellingPrice);

            // Baseline cost = same total consumption at weighted avg buy price without solar/battery.
            stats.BaselineEnergyCostEur = stats.TotalConsumptionKWh * stats.WeightedAvgBuyPriceEurPerKWh;
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