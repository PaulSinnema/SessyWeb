using Microsoft.Extensions.Options;
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
    /// Uses QuarterlyMeasurement as the single source of truth.
    ///
    /// Unit conventions:
    ///   QuarterlyMeasurement.GridImportWh/GridExportWh  — Wh per quarter → / 1000 for kWh
    ///   QuarterlyMeasurement.SolarProductionKWh         — kWh per quarter (already kWh)
    ///   QuarterlyMeasurement.BatteryPowerWatts           — Watts; negative=charging, positive=discharging
    ///   QuarterlyMeasurement.BatteryStateOfChargeWh      — Wh
    /// </summary>
    public class EnergyStatisticsService
    {
        private readonly QuarterlyMeasurementDataService _measurementDataService;
        private readonly InvestmentDataService _investmentDataService;
        private readonly EnergyHistoryDataService _energyHistoryDataService;
        private readonly TimeZoneService _timeZoneService;
        private readonly HeatPumpConfig _heatPumpConfig;
        private readonly SettingsConfig _settingsConfig;
        private readonly PowerSystemsConfig _powerSystemsConfig;

        // Total battery capacity in kWh for cycle calculation (3x Sessy 5.4 kWh).
        private const double BatteryCapacityKWh = 16.2;

        // Category name constants for per-component savings routing.
        private const string CategorySolar = "solarpanels";
        private const string CategoryBattery = "battery";
        private const string CategoryHeatPump = "heatpump";

        private static string NormalizeCategory(string category)
        {
            var normalized = category.ToLowerInvariant()
                .Replace(" ", "")
                .Replace("-", "")
                .Replace("_", "");

            return normalized switch
            {
                "solar" or "zonnepanelen" or "panelen" or "solaredge" or "enphase" or "huawei" or "sma" => CategorySolar,
                "batterij" or "batteries" or "battery"
                    or "sessy" or "sessy1" or "sessy2" or "sessy3"
                    or "sessy2+3" or "sessy1+2" or "sessy1+2+3" => CategoryBattery,
                "warmtepomp" or "heatpump" or "hp" or "daikin" or "daikinaltherma"
                    or "mitsubishi" or "lg" or "vaillant" or "nibe" => CategoryHeatPump,
                _ => normalized
            };
        }

        public EnergyStatisticsService(QuarterlyMeasurementDataService measurementDataService,
                                       InvestmentDataService investmentDataService,
                                       EnergyHistoryDataService energyHistoryDataService,
                                       TimeZoneService timeZoneService,
                                       IOptions<HeatPumpConfig> heatPumpConfig,
                                       IOptions<SettingsConfig> settingsConfig,
                                       IOptions<PowerSystemsConfig> powerSystemsConfig)
        {
            _measurementDataService = measurementDataService;
            _investmentDataService = investmentDataService;
            _energyHistoryDataService = energyHistoryDataService;
            _timeZoneService = timeZoneService;
            _heatPumpConfig = heatPumpConfig.Value;
            _settingsConfig = settingsConfig.Value;
            _powerSystemsConfig = powerSystemsConfig.Value;
        }

        /// <summary>
        /// Calculates comprehensive energy statistics for the given period.
        /// When StatisticsFromDate is configured, the start date is clamped to that date.
        /// Only complete days are included: the first and last day are excluded when
        /// they contain no data from 00:00 to 23:45, avoiding distorted averages.
        /// </summary>
        public async Task<EnergyStatistics> GetEnergyStatisticsAsync(DateTime start, DateTime end)
        {
            if (_settingsConfig.StatisticsFromDate.HasValue)
            {
                var fromDate = _settingsConfig.StatisticsFromDate.Value;
                if (start == DateTime.MinValue || start < fromDate)
                    start = fromDate;
            }

            var measurements = await GetMeasurementsAsync(start, end);

            // Exclude incomplete first and last days.
            // A day is complete when it has data from 00:00 through 23:45 (96 quarters).
            // In practice: skip the first day if it starts after 00:00,
            // and skip the last day if it ends before 23:45.
            if (measurements.Any())
            {
                var firstTime = measurements.Min(m => m.Time);
                var lastTime = measurements.Max(m => m.Time);

                // First day incomplete if data doesn't start at midnight.
                if (firstTime.TimeOfDay > TimeSpan.Zero)
                {
                    var firstFullDay = firstTime.Date.AddDays(1);
                    measurements = measurements.Where(m => m.Time >= firstFullDay).ToList();
                }

                // Last day incomplete if data doesn't end at 23:45.
                if (lastTime.TimeOfDay < new TimeSpan(23, 45, 0))
                {
                    var lastFullDay = lastTime.Date;
                    measurements = measurements.Where(m => m.Time < lastFullDay).ToList();
                }
            }

            var stats = new EnergyStatistics
            {
                PeriodStart = start,
                PeriodEnd = end,
                ActualDataStart = measurements.Any() ? measurements.Min(m => m.Time) : start,
                ActualDataEnd = measurements.Any() ? measurements.Max(m => m.Time) : end,
            };

            // Order matters: self-consumed solar depends on battery totals.
            CalculateGridFlows(measurements, stats);
            CalculateSolarStats(measurements, stats);
            CalculateConsumptionStats(measurements, stats);
            CalculateBatteryStats(measurements, stats);
            CalculateSelfConsumedSolar(stats);
            CalculateFinancialStats(measurements, stats);

            return stats;
        }

        public async Task<List<MonthlyTrend>> GetMonthlyTrendsAsync(DateTime start, DateTime end)
        {
            var trends = new List<MonthlyTrend>();

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
            var dataStart = await GetEarliestDataDateAsync();

            // Calculate weighted average buying price from measured data.
            // Used for heat pump electricity cost calculation when no price is configured.
            var allMeasurements = await GetMeasurementsAsync(dataStart, now);
            double avgBuyPrice = allMeasurements.Any(m => m.GridImportWh > 0)
                ? allMeasurements.Where(m => m.GridImportWh > 0)
                    .Average(m => m.BuyingPriceEur)
                : 0.25; // fallback

            // Set effective electricity price on config object.
            _heatPumpConfig.EffectiveElectricityPriceEurPerKWh =
                _heatPumpConfig.ElectricityPriceEurPerKWh > 0
                    ? _heatPumpConfig.ElectricityPriceEurPerKWh
                    : avgBuyPrice;

            var monthlySolarSavings = await BuildSeasonalSolarSavingsAsync(dataStart, now);
            var monthlyArbitrageSavings = await BuildSeasonalArbitrageSavingsAsync(dataStart, now);

            var categoryBreakdown = investments
                .GroupBy(i => i.Category)
                .Select(g =>
                {
                    var installDate = g.Min(i => i.PurchaseDate);
                    var monthsSince = (now.Year - installDate.Year) * 12 +
                                      (now.Month - installDate.Month);

                    double annualSavings = CalculateCategoryAnnualSavings(
                        g.Key, g.First(), monthlySolarSavings, monthlyArbitrageSavings);

                    return new InvestmentCategoryStats
                    {
                        Category = g.Key,
                        TotalAmountEur = g.Sum(i => i.AmountEur),
                        TotalSubsidyEur = g.Sum(i => i.SubsidyEur),
                        ExpectedLifetimeYears = (int)g.Average(i => i.ExpectedLifetimeYears),
                        InstallationDate = installDate,
                        MonthsSinceInstallation = monthsSince,
                        AnnualSavingsEur = annualSavings,
                        SavingsSource = GetSavingsSource(g.Key, g.First())
                    };
                })
                .OrderByDescending(c => c.NetAmountEur)
                .ToList();

            double totalNetInvestment = categoryBreakdown.Sum(c => c.NetAmountEur);
            double totalProjectedSavings = categoryBreakdown.Sum(c => c.ProjectedTotalSavingsEur);
            double totalAnnualSavings = categoryBreakdown.Sum(c => c.AnnualSavingsEur);

            // Realized savings = sum of projected savings per category since install date.
            // This extrapolates measured annual savings back to the installation date,
            // giving a realistic picture of how much has been saved over the full period.
            double realizedSavings = totalProjectedSavings;

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

        // ── Private helpers ──────────────────────────────────────────────────

        private double CalculateCategoryAnnualSavings(
            string category,
            Investment representative,
            Dictionary<int, double> monthlySolarSavings,
            Dictionary<int, double> monthlyArbitrageSavings)
        {
            if (representative.EstimatedAnnualSavingsEur > 0)
                return representative.EstimatedAnnualSavingsEur;

            return NormalizeCategory(category) switch
            {
                CategorySolar => monthlySolarSavings.Values.Sum(),
                CategoryBattery => monthlyArbitrageSavings.Values.Sum(),
                CategoryHeatPump => _heatPumpConfig.IsConfigured ? _heatPumpConfig.TotalAnnualSavingsEur : 0.0,
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
                    ? $"{_heatPumpConfig.AnnualGasConsumptionM3} m³ × €{_heatPumpConfig.GasPriceEurPerM3}/m³ + €{_heatPumpConfig.GasStandingChargeEurPerYear} vastrecht - {_heatPumpConfig.AnnualElectricityConsumptionKWh} kWh × €{_heatPumpConfig.EffectiveElectricityPriceEurPerKWh:F2}/kWh elektra"
                    : "Not configured — add HeatPumpConfig to appsettings.json",
                _ => "Manual estimate required — set EstimatedAnnualSavingsEur on investment"
            };
        }

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
        /// Forecasts annual solar production in kWh based on:
        /// 1. Measured performance ratio (PR) from QuarterlyMeasurements
        /// 2. KNMI monthly radiation norms for De Bilt
        /// 3. Total solar installation kWp from PowerSystemsConfig
        ///
        /// PR = actual_kWh / (radiation_kWh/m2 * kWp)
        /// forecast(month) = PR * KNMI_norm(month) / 1000 * kWp * hours_in_month
        ///
        /// Falls back to SolarAnnualProductionKWh from config when insufficient data.
        /// </summary>
        private async Task<double> ForecastAnnualSolarProductionKWhAsync()
        {
            // KNMI monthly average global radiation norms for De Bilt (W/m²).
            var knmiNorms = new Dictionary<int, double>
            {
                { 1, 26 }, { 2, 47 }, { 3, 94 }, { 4, 141 }, { 5, 185 },
                { 6, 198 }, { 7, 185 }, { 8, 157 }, { 9, 105 }, { 10, 62 },
                { 11, 30 }, { 12, 20 }
            };

            var daysInMonth = new Dictionary<int, int>
            {
                { 1, 31 }, { 2, 28 }, { 3, 31 }, { 4, 30 }, { 5, 31 }, { 6, 30 },
                { 7, 31 }, { 8, 31 }, { 9, 30 }, { 10, 31 }, { 11, 30 }, { 12, 31 }
            };

            // Total kWp from PowerSystemsConfig.
            double kWp = _powerSystemsConfig.Endpoints?
                .SelectMany(p => p.Value.Values)
                .Where(ep => ep.SolarPanels != null)
                .SelectMany(ep => ep.SolarPanels!.Values)
                .Sum(pv => pv.PeakPowerForArray) / 1000.0 ?? 0.0;

            if (kWp <= 0)
                return _settingsConfig.SolarAnnualProductionKWh;

            // Calculate measured performance ratio from available QuarterlyMeasurements.
            var measurements = await GetMeasurementsAsync(
                _timeZoneService.Now.AddYears(-1), _timeZoneService.Now);

            var withRadiation = measurements
                .Where(m => m.GlobalRadiation > 0 && m.SolarProductionKWh > 0)
                .ToList();

            if (!withRadiation.Any())
                return _settingsConfig.SolarAnnualProductionKWh;

            // PR = measured kWh / theoretical kWh
            double measuredKWh = withRadiation.Sum(m => m.SolarProductionKWh);
            double theoreticalKWh = withRadiation.Sum(m => m.GlobalRadiation / 1000.0 * kWp * 0.25);
            double pr = theoreticalKWh > 0 ? measuredKWh / theoreticalKWh : 0.0;

            if (pr <= 0 || pr > 1.0)
                return _settingsConfig.SolarAnnualProductionKWh;

            // Forecast annual production using KNMI norms.
            double forecast = 0.0;
            foreach (var (month, norm) in knmiNorms)
            {
                double hoursInMonth = daysInMonth[month] * 24.0;
                forecast += norm / 1000.0 * kWp * hoursInMonth * pr;
            }

            // Blend with configured value when less than 3 months of data available.
            double measuredMonths = withRadiation
                .Select(m => new { m.Time.Year, m.Time.Month })
                .Distinct()
                .Count();

            if (measuredMonths < 3 && _settingsConfig.SolarAnnualProductionKWh > 0)
            {
                double blend = measuredMonths / 3.0;
                forecast = forecast * blend +
                           _settingsConfig.SolarAnnualProductionKWh * (1.0 - blend);
            }

            return Math.Round(forecast, 0);
        }

        private async Task<Dictionary<int, double>> BuildSeasonalSolarSavingsAsync(
            DateTime dataStart, DateTime dataEnd)
        {
            var allMonthly = new Dictionary<int, List<double>>();
            var current = new DateTime(dataStart.Year, dataStart.Month, 1);

            // Determine where QuarterlyMeasurements start.
            var firstMeasurement = await _measurementDataService.Get(async set =>
                await Task.FromResult(set.OrderBy(m => m.Time).FirstOrDefault()));
            var measurementStart = firstMeasurement?.Time ?? dataEnd;

            while (current <= dataEnd)
            {
                var monthEnd = new DateTime(Math.Min(current.AddMonths(1).AddTicks(-1).Ticks, dataEnd.Ticks));
                int month = current.Month;
                double monthlySaving = 0.0;

                if (current < measurementStart)
                {
                    // ── EnergyHistory period ──────────────────────────────────
                    // Use net export revenue as solar savings proxy.
                    // Net export = produced - consumed (negative = net import = cost).
                    var histories = await _energyHistoryDataService.GetList(async set =>
                        await Task.FromResult(set
                            .Where(h => h.Time >= current && h.Time < monthEnd)
                            .OrderBy(h => h.Time)
                            .ToList()));

                    if (histories.Count >= 2)
                    {
                        var first = histories.First();
                        var last = histories.Last();

                        double exportWh = (last.ProducedTariff1 - first.ProducedTariff1)
                                        + (last.ProducedTariff2 - first.ProducedTariff2);
                        double importWh = (last.ConsumedTariff1 - first.ConsumedTariff1)
                                        + (last.ConsumedTariff2 - first.ConsumedTariff2);

                        // Estimate average prices from EPEXPrices via CalculationService.
                        // Use a fixed estimate of 0.25 EUR/kWh buy, 0.08 EUR/kWh sell
                        // for the historical period (before dynamic pricing was tracked).
                        const double historicalBuyEur = 0.25;
                        const double historicalSellEur = 0.08;

                        // Export revenue + import savings (self-consumption proxy).
                        double netExportKWh = Math.Max(0, exportWh / 1000.0);
                        double selfConsumedProxy = Math.Max(0, (exportWh - importWh) / 1000.0);

                        monthlySaving = netExportKWh * historicalSellEur
                                      + Math.Max(0, selfConsumedProxy) * historicalBuyEur;
                    }
                }
                else
                {
                    // ── QuarterlyMeasurements period ─────────────────────────
                    var measurements = await GetMeasurementsAsync(current, monthEnd);

                    double exportRevenue = measurements
                        .Where(m => m.BatteryMode == BatteryMode.Discharging)
                        .Sum(m => m.SellingPriceEur * m.BatteryDischargedKWh);

                    double solarKWh = measurements.Sum(m => m.SolarProductionKWh);
                    double avgBuyPrice = measurements.Any()
                        ? measurements.Average(m => m.BuyingPriceEur)
                        : 0.0;

                    monthlySaving = exportRevenue + solarKWh * avgBuyPrice;
                }

                if (!allMonthly.ContainsKey(month))
                    allMonthly[month] = new List<double>();

                allMonthly[month].Add(monthlySaving);
                current = current.AddMonths(1);
            }

            var result = new Dictionary<int, double>();
            double overallAvg = allMonthly.Values.SelectMany(v => v).DefaultIfEmpty(0).Average();

            for (int m = 1; m <= 12; m++)
                result[m] = allMonthly.ContainsKey(m) ? allMonthly[m].Average() : overallAvg;

            // ── Scale using forecasted annual production ──────────────────────
            // Uses measured PR + KNMI radiation norms to forecast annual production.
            // Falls back to SolarAnnualProductionKWh from config when insufficient data.
            double knownAnnualKWh = await ForecastAnnualSolarProductionKWhAsync();

            if (knownAnnualKWh > 0)
            {
                var recentMeasurements = await GetMeasurementsAsync(measurementStart, dataEnd);
                double measuredMonths = recentMeasurements
                    .Select(m => new { m.Time.Year, m.Time.Month })
                    .Distinct()
                    .Count();

                if (measuredMonths < 6)
                {
                    double avgBuy = recentMeasurements.Any(m => m.BuyingPriceEur > 0)
                        ? recentMeasurements.Average(m => m.BuyingPriceEur)
                        : 0.27;

                    double measuredAnnualKWh = result.Values.Sum() / Math.Max(avgBuy, 0.01);

                    if (measuredAnnualKWh > 0)
                    {
                        double scaleFactor = knownAnnualKWh / measuredAnnualKWh;
                        double blend = 1.0 - (measuredMonths / 6.0);
                        double effectiveFactor = 1.0 + (scaleFactor - 1.0) * blend;

                        for (int m = 1; m <= 12; m++)
                            result[m] *= effectiveFactor;
                    }
                }
            }

            return result;
        }

        private async Task<Dictionary<int, double>> BuildSeasonalArbitrageSavingsAsync(
            DateTime dataStart, DateTime dataEnd)
        {
            var allMonthly = new Dictionary<int, List<double>>();
            var current = new DateTime(dataStart.Year, dataStart.Month, 1);

            while (current <= dataEnd)
            {
                var clampedEnd = Math.Min(current.AddMonths(1).AddTicks(-1).Ticks, dataEnd.Ticks);
                var measurements = await GetMeasurementsAsync(current, new DateTime(clampedEnd));

                double revenue = measurements
                    .Where(m => m.BatteryMode == BatteryMode.Discharging)
                    .Sum(m => m.SellingPriceEur * m.BatteryDischargedKWh);

                double cost = measurements
                    .Where(m => m.BatteryMode == BatteryMode.Charging)
                    .Sum(m => m.BuyingPriceEur * m.BatteryChargedKWh);

                int month = current.Month;
                if (!allMonthly.ContainsKey(month))
                    allMonthly[month] = new List<double>();

                allMonthly[month].Add(revenue - cost);
                current = current.AddMonths(1);
            }

            var result = new Dictionary<int, double>();
            double overallAvg = allMonthly.Values.SelectMany(v => v).DefaultIfEmpty(0).Average();

            for (int m = 1; m <= 12; m++)
                result[m] = allMonthly.ContainsKey(m) ? allMonthly[m].Average() : overallAvg;

            return result;
        }

        private async Task<DateTime> GetEarliestDataDateAsync()
        {
            // Use the earlier of QuarterlyMeasurements and EnergyHistory as data start.
            // EnergyHistory contains archived P1 readings back to March 2025.
            var earliestMeasurement = await _measurementDataService.Get(async set =>
            {
                var result = set.OrderBy(m => m.Time).FirstOrDefault();
                return await Task.FromResult(result);
            });

            var earliestHistory = await _energyHistoryDataService.Get(async set =>
            {
                var result = set.OrderBy(h => h.Time).FirstOrDefault();
                return await Task.FromResult(result);
            });

            var candidates = new List<DateTime>();
            if (earliestMeasurement != null) candidates.Add(earliestMeasurement.Time);
            if (earliestHistory != null) candidates.Add(earliestHistory.Time);

            return candidates.Any() ? candidates.Min() : _timeZoneService.Now.AddYears(-1);
        }

        // ── Calculation methods ──────────────────────────────────────────────

        /// <summary>
        /// Grid flows = sum of per-quarter deltas stored in GridImportWh / GridExportWh.
        /// </summary>
        private static void CalculateGridFlows(
            List<QuarterlyMeasurement> measurements, EnergyStatistics stats)
        {
            stats.TotalGridImportKWh = measurements.Sum(m => m.GridImportKWh);
            stats.TotalGridExportKWh = measurements.Sum(m => m.GridExportKWh);
        }

        private void CalculateSolarStats(
            List<QuarterlyMeasurement> measurements, EnergyStatistics stats)
        {
            if (!measurements.Any())
                return;

            stats.TotalSolarProductionKWh = measurements.Sum(m => m.SolarProductionKWh);

            stats.PeakDailySolarProductionKWh = measurements
                .GroupBy(m => m.Time.Date)
                .Select(g => g.Sum(m => m.SolarProductionKWh))
                .DefaultIfEmpty(0)
                .Max();

            // Performance ratio: actual vs theoretical based on KNMI global radiation.
            // Total solar installation kWp = sum of all panel strings across all inverters.
            // PeakPowerForArray = PanelCount * PeakPowerPerPanel * Efficiency (in Watts).
            double solarInstallationKWp = _powerSystemsConfig.Endpoints?
                .SelectMany(provider => provider.Value.Values)
                .Where(ep => ep.SolarPanels != null)
                .SelectMany(ep => ep.SolarPanels!.Values)
                .Sum(pv => pv.PeakPowerForArray) / 1000.0 ?? 0.0;

            double theoreticalKWh = measurements
                .Sum(m => m.GlobalRadiation / 1000.0 * solarInstallationKWp * 0.25);

            stats.SolarPerformanceRatio = theoreticalKWh > 0
                ? stats.TotalSolarProductionKWh / theoreticalKWh
                : 0.0;
        }

        /// <summary>
        /// Self-consumed solar = solar - solar exported to grid.
        /// Solar exported = max(0, grid export - battery discharged).
        /// Must be called AFTER CalculateBatteryStats.
        /// </summary>
        private static void CalculateSelfConsumedSolar(EnergyStatistics stats)
        {
            if (stats.TotalSolarProductionKWh <= 0)
            {
                stats.SelfConsumedSolarKWh = 0;
                return;
            }

            double batteryContributionToExport = Math.Min(
                stats.TotalBatteryDischargedKWh,
                stats.TotalGridExportKWh);

            double solarExportedKWh = Math.Max(0,
                stats.TotalGridExportKWh - batteryContributionToExport);

            stats.SelfConsumedSolarKWh = Math.Max(0, Math.Min(
                stats.TotalSolarProductionKWh - solarExportedKWh,
                stats.TotalSolarProductionKWh));
        }

        private static void CalculateConsumptionStats(
            List<QuarterlyMeasurement> measurements, EnergyStatistics stats)
        {
            // Total consumption via energy balance.
            stats.TotalConsumptionKWh = Math.Max(0,
                stats.TotalGridImportKWh +
                stats.TotalSolarProductionKWh -
                stats.TotalGridExportKWh);

            // Peak daily consumption from per-quarter grid import.
            stats.PeakDailyConsumptionKWh = measurements
                .GroupBy(m => m.Time.Date)
                .Select(g => g.Sum(m => m.GridImportKWh + m.SolarProductionKWh - m.GridExportKWh))
                .Where(v => v > 0)
                .DefaultIfEmpty(0)
                .Max();

            // Weekday vs weekend from grid import + solar - export per quarter.
            stats.WeekdayConsumptionKWh = measurements
                .Where(m => m.Time.DayOfWeek != DayOfWeek.Saturday &&
                            m.Time.DayOfWeek != DayOfWeek.Sunday)
                .Sum(m => Math.Max(0, m.GridImportKWh + m.SolarProductionKWh - m.GridExportKWh));

            stats.WeekendConsumptionKWh = measurements
                .Where(m => m.Time.DayOfWeek == DayOfWeek.Saturday ||
                            m.Time.DayOfWeek == DayOfWeek.Sunday)
                .Sum(m => Math.Max(0, m.GridImportKWh + m.SolarProductionKWh - m.GridExportKWh));
        }

        private void CalculateBatteryStats(
            List<QuarterlyMeasurement> measurements, EnergyStatistics stats)
        {
            if (!measurements.Any())
                return;

            // All records for energy balance.
            stats.TotalBatteryChargedKWh = measurements.Sum(m => m.BatteryChargedKWh);
            stats.TotalBatteryDischargedKWh = measurements.Sum(m => m.BatteryDischargedKWh);

            // Reliable records only for round-trip efficiency.
            var reliable = measurements.Where(m => m.IsReliable).ToList();
            stats.ReliableBatteryChargedKWh = reliable.Sum(m => m.BatteryChargedKWh);
            stats.ReliableBatteryDischargedKWh = reliable.Sum(m => m.BatteryDischargedKWh);

            stats.BatteryCycles = BatteryCapacityKWh > 0
                ? stats.TotalBatteryChargedKWh / BatteryCapacityKWh
                : 0.0;

            var withSoc = measurements.Where(m => m.BatteryStateOfChargeWh > 0).ToList();
            stats.AverageSocPct = withSoc.Any()
                ? withSoc.Average(m => m.BatteryStateOfChargeWh) / (BatteryCapacityKWh * 1000.0) * 100.0
                : 0.0;
        }

        private static void CalculateFinancialStats(
            List<QuarterlyMeasurement> measurements, EnergyStatistics stats)
        {
            if (!measurements.Any())
                return;

            // Charging cost: energy bought from grid to charge batteries.
            double chargingCostEur = measurements
                .Where(m => m.BatteryMode == BatteryMode.Charging)
                .Sum(m => m.BuyingPriceEur * m.BatteryChargedKWh);

            // Import cost: energy bought for household consumption.
            double importCostEur = measurements
                .Where(m => m.BatteryMode == BatteryMode.ZeroNetHome || m.BatteryMode == BatteryMode.Disabled)
                .Sum(m => m.BuyingPriceEur * m.GridImportKWh);

            // Export revenue: energy sold to grid.
            stats.GridExportRevenueEur = measurements
                .Where(m => m.BatteryMode == BatteryMode.Discharging)
                .Sum(m => m.SellingPriceEur * m.BatteryDischargedKWh);

            stats.ActualEnergyCostEur = importCostEur + chargingCostEur - stats.GridExportRevenueEur;
            stats.ArbitrageProfitEur = stats.GridExportRevenueEur - chargingCostEur;

            // Weighted average prices.
            double totalImportKWh = measurements.Sum(m => m.GridImportKWh);

            stats.WeightedAvgBuyPriceEurPerKWh = totalImportKWh > 0
                ? measurements.Sum(m => m.BuyingPriceEur * m.GridImportKWh) / totalImportKWh
                : measurements.Average(m => m.BuyingPriceEur);

            double totalDischargedKWh = measurements.Sum(m => m.BatteryDischargedKWh);

            stats.WeightedAvgSellPriceEurPerKWh = totalDischargedKWh > 0
                ? measurements
                    .Where(m => m.BatteryMode == BatteryMode.Discharging)
                    .Sum(m => m.SellingPriceEur * m.BatteryDischargedKWh) / totalDischargedKWh
                : measurements.Average(m => m.SellingPriceEur);

            stats.BaselineEnergyCostEur =
                stats.TotalConsumptionKWh * stats.WeightedAvgBuyPriceEurPerKWh;
        }

        // ── Data access ──────────────────────────────────────────────────────

        private async Task<List<QuarterlyMeasurement>> GetMeasurementsAsync(
            DateTime start, DateTime end)
        {
            return await _measurementDataService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= start && m.Time <= end)
                    .OrderBy(m => m.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }
    }
}