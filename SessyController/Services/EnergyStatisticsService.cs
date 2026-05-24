using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.StateMachine;
using SessyController.Services.Statistics;
using SessyData.Model;
using SessyData.Services;
using System.Text.RegularExpressions;

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
        private readonly EPEXPricesDataService _epexDataService;
        private readonly InvestmentGroupDataService _groupService;
        private readonly TimeZoneService _timeZoneService;
        private readonly HeatPumpConfig _heatPumpConfig;
        private readonly SettingsConfig _settingsConfig;
        private readonly PowerSystemsConfig _powerSystemsConfig;
        private readonly IEPEXPricesService _epexPricesService;
        private readonly IGasPricesDataService _gasPricesDataService;
        private readonly ConsumptionDataService _consumptionDataService;
        private readonly ICalculationService _calculationService;
        private readonly IBatteryContainer _batteryContainer;
        private readonly IMilpService _milpService;
        private readonly HardwareStatusService _hardwareStatusService;

        // Convenience property: total battery capacity in kWh from BatteryContainer.
        private double BatteryCapacityKWh => _batteryContainer.GetTotalCapacity() / 1000.0;

        // Category name constants for per-component savings routing.
        public EnergyStatisticsService(QuarterlyMeasurementDataService measurementDataService,
                                       InvestmentDataService investmentDataService,
                                       EnergyHistoryDataService energyHistoryDataService,
                                       EPEXPricesDataService epexDataService,
                                       InvestmentGroupDataService groupService,
                                       TimeZoneService timeZoneService,
                                       IOptions<HeatPumpConfig> heatPumpConfig,
                                       IOptions<SettingsConfig> settingsConfig,
                                       IOptions<PowerSystemsConfig> powerSystemsConfig,
                                       IEPEXPricesService epexPricesService,
                                       IGasPricesDataService gasPricesDataService,
                                       ConsumptionDataService consumptionDataService,
                                       ICalculationService calculationService,
                                       IBatteryContainer batteryContainer,
                                       IMilpService milpService,
                                       HardwareStatusService hardwareStatusService)
        {
            _measurementDataService = measurementDataService;
            _investmentDataService = investmentDataService;
            _energyHistoryDataService = energyHistoryDataService;
            _epexDataService = epexDataService;
            _groupService = groupService;
            _timeZoneService = timeZoneService;
            _heatPumpConfig = heatPumpConfig.Value;
            _settingsConfig = settingsConfig.Value;
            _powerSystemsConfig = powerSystemsConfig.Value;
            _epexPricesService = epexPricesService;
            _gasPricesDataService = gasPricesDataService;
            _consumptionDataService = consumptionDataService;
            _calculationService = calculationService;
            _batteryContainer = batteryContainer;
            _milpService = milpService;
            _hardwareStatusService = hardwareStatusService;
        }

        /// <summary>
        /// Returns the effective gas price in EUR/m³ for savings calculations.
        /// Priority:
        /// 1. Historical average all-in price from GasPrices table (most stable)
        /// 2. Live price from EPEXPricesService (today's value, all-in)
        /// 3. Configured fallback from HeatPumpConfig (appsettings.json)
        /// </summary>
        private async Task<(double price, string source)> GetEffectiveGasPriceAsync()
        {
            // Try heating-degree-day weighted historical average — most accurate.
            // Colder days (more heating demand) carry more weight in the average,
            // reflecting actual gas consumption patterns.
            var consumptionData = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            double? avgMarketPrice = consumptionData.Any()
                ? await _gasPricesDataService.GetHeatingWeightedAverageMarketPriceAsync(consumptionData)
                : await _gasPricesDataService.GetAverageMarketPriceAsync();

            if (avgMarketPrice.HasValue)
            {
                // Apply current taxes to the weighted average market price.
                double? allInAvg = await _calculationService.CalculateGasPriceAsync(avgMarketPrice.Value);
                double price = allInAvg ?? avgMarketPrice.Value;
                int days = (await _gasPricesDataService.GetAllAsync()).Count;
                string src = $"Heating-degree-day weighted average of {days} days: market avg={avgMarketPrice.Value:F4} EUR/m³, all-in={price:F4} EUR/m³ (incl. energiebelasting + BTW)";
                return (price, src);
            }

            // Fall back to today's live price.
            if (_epexPricesService.CurrentGasPriceEurPerM3.HasValue)
            {
                double price = _epexPricesService.CurrentGasPriceEurPerM3.Value;
                string src = $"Live TTF day-ahead via Enever.nl, all-in incl. energiebelasting + BTW";
                return (price, src);
            }

            // Last resort: configured value.
            return (_heatPumpConfig.GasPriceEurPerM3,
                    $"Configured fallback (no live feed or history available — add Enever:Token to appsettings.json): € {_heatPumpConfig.GasPriceEurPerM3:F4}/m³");
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

            // Exclude incomplete first and last days only when sufficient complete days remain.
            // A day is complete when it has data from 00:00 through 23:45 (96 quarters).
            if (measurements.Any())
            {
                var firstTime = measurements.Min(m => m.Time);
                var lastTime = measurements.Max(m => m.Time);

                // First day incomplete if data doesn't start at midnight.
                if (firstTime.TimeOfDay > TimeSpan.Zero)
                {
                    var firstFullDay = firstTime.Date.AddDays(1);
                    var afterFilter = measurements.Where(m => m.Time >= firstFullDay).ToList();
                    // Only apply if complete days remain after filtering.
                    if (afterFilter.Any())
                        measurements = afterFilter;
                }

                // Re-evaluate after potential first-day trim.
                lastTime = measurements.Any() ? measurements.Max(m => m.Time) : lastTime;

                // Last day incomplete if data doesn't end at 23:45.
                if (lastTime.TimeOfDay < new TimeSpan(23, 45, 0))
                {
                    var lastFullDay = lastTime.Date;
                    var afterFilter = measurements.Where(m => m.Time < lastFullDay).ToList();
                    // Only apply if complete days remain after filtering.
                    if (afterFilter.Any())
                        measurements = afterFilter;
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
            CalculateSelfConsumedSolar(measurements, stats);
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

        /// Returns per-day planned vs realized arbitrage profit for the given period.
        /// Only days with at least one QuarterlyMeasurement are included.
        /// </summary>
        public async Task<List<DailyArbitrageTrend>> GetDailyArbitrageTrendsAsync(DateTime start, DateTime end)
        {
            var clampedStart = start == DateTime.MinValue ? _timeZoneService.Now.AddDays(-30) : start;
            var clampedEnd = end == DateTime.MaxValue ? _timeZoneService.Now : end;

            var measurements = await GetMeasurementsAsync(clampedStart, clampedEnd);

            return measurements
                .GroupBy(m => m.Time.Date)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    // Realized: discharge revenue - charge cost
                    double realized = g.Sum(m =>
                        m.BatteryDischargedKWh * m.SellingPriceEur
                        - m.BatteryChargedKWh * m.BuyingPriceEur);

                    double planned = g.Sum(m => m.PlannedRevenueEur);

                    return new DailyArbitrageTrend
                    {
                        Date = g.Key,
                        RealizedEur = Math.Round(realized, 4),
                        PlannedEur = Math.Round(planned, 4)
                    };
                })
                .Where(d => d.PlannedEur != 0 || d.RealizedEur != 0)
                .ToList();
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

            var allMeasurements = await GetMeasurementsAsync(dataStart, now);

            var monthlySolarSavings = await BuildSeasonalSolarSavingsAsync(dataStart, now);
            var monthlyArbitrageSavings = await BuildSeasonalArbitrageSavingsAsync(dataStart, now);

            // Load all investment groups for display name and category lookup.
            var groups = await _groupService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            var groupNameById = groups.ToDictionary(g => g.Id, g => g.Name);
            var groupCategoryById = groups.ToDictionary(g => g.Id, g => g.Category);

            // Helper: get the InvestmentCategory for an investment.
            // Ungrouped investments fall back to Other.
            InvestmentCategory GetCategory(Investment inv) =>
                inv.InvestmentGroupId.HasValue && groupCategoryById.TryGetValue(inv.InvestmentGroupId.Value, out var cat)
                    ? cat
                    : InvestmentCategory.Other;

            // Total net investment in Storage category — used to prorate arbitrage savings.
            double totalBatteryNetInvestment = investments
                .Where(i => GetCategory(i) == InvestmentCategory.Storage)
                .Sum(i => i.AmountEur - i.SubsidyEur);

            double totalArbitrageSavings = monthlyArbitrageSavings.Values.Sum();

            // Group investments by InvestmentGroupId (when set) or by individual Id.
            var grouped = investments
                .GroupBy(i => i.InvestmentGroupId.HasValue
                    ? $"__group_{i.InvestmentGroupId.Value}"
                    : $"__single_{i.Id}");

            var categoryBreakdown = grouped
                .Select(g =>
                {
                    var installDate = g.Min(i => i.PurchaseDate);
                    var monthsSince = (now.Year - installDate.Year) * 12 +
                                      (now.Month - installDate.Month);

                    // Get category from the group definition.
                    var first = g.First();
                    bool isGroup = first.InvestmentGroupId.HasValue;
                    var category = GetCategory(first);

                    bool hasSolar = category == InvestmentCategory.Solar;
                    bool hasBattery = category == InvestmentCategory.Storage;

                    double annualSavings;
                    string savingsSource;

                    // Solar and battery are always treated as one integrated energy system.
                    // Their savings cannot be reliably attributed separately (battery charges
                    // from both solar and grid; arbitrage and self-consumption interact).
                    if (hasSolar || hasBattery)
                    {
                        double solarAnnual = monthlySolarSavings.Values.Sum();
                        double batteryAnnual = monthlyArbitrageSavings.Values.Sum();
                        annualSavings = solarAnnual + batteryAnnual;
                        savingsSource = "Energy system savings: baseline cost minus actual cost (solar + battery combined)";
                    }
                    {
                        annualSavings = CalculateCategoryAnnualSavings(
                            category, first, monthlySolarSavings, monthlyArbitrageSavings);
                        savingsSource = GetSavingsSource(category, first);
                    }

                    // Display name: group name from lookup, or investment description.
                    string displayCategory;
                    if (isGroup && first.InvestmentGroupId.HasValue
                        && groupNameById.TryGetValue(first.InvestmentGroupId.Value, out var gName))
                        displayCategory = gName;
                    else
                        displayCategory = first.Description;

                    return new InvestmentCategoryStats
                    {
                        Category = displayCategory,
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
                if (remaining <= 0)
                {
                    breakEvenDate = now;
                }
                else
                {
                    double days = remaining / totalAnnualSavings * 365.0;
                    if (days <= 3_000_000)
                        breakEvenDate = now.AddDays(days);
                }
            }

            // ComponentBreakdown: each investment individually, regardless of group.
            // Calculate cyclesPerDay from measurements for battery cycle extrapolation.
            var cycleMeasurements = await GetMeasurementsAsync(dataStart, now);
            double totalChargedKWh = cycleMeasurements.Sum(m => m.BatteryChargedKWh);
            int measuredDays = Math.Max(1, cycleMeasurements
                .Select(m => m.Time.Date)
                .Distinct()
                .Count());
            double cyclesPerDay = BatteryCapacityKWh > 0
                ? totalChargedKWh / BatteryCapacityKWh / measuredDays
                : 0.0;

            // Combined annual savings for the integrated solar + battery system.
            double CombinedEnergySystemSavings() =>
                monthlySolarSavings.Values.Sum() + monthlyArbitrageSavings.Values.Sum();

            var componentBreakdown = investments
                .Select(inv =>
                {
                    var installDate = inv.PurchaseDate;
                    var monthsSince = (now.Year - installDate.Year) * 12 +
                                      (now.Month - installDate.Month);
                    var category = GetCategory(inv);

                    double annualSavings;
                    string savingsSource;

                    if (inv.InvestmentGroupId.HasValue)
                    {
                        // Prorate group savings by this investment's share of group net investment.
                        var groupInvestments = investments
                            .Where(i => i.InvestmentGroupId == inv.InvestmentGroupId)
                            .ToList();
                        double groupNet = groupInvestments.Sum(i => i.AmountEur - i.SubsidyEur);
                        double share = groupNet > 0 ? (inv.AmountEur - inv.SubsidyEur) / groupNet : 1.0;

                        // Find this investment's group in categoryBreakdown.
                        var groupStats = categoryBreakdown
                            .FirstOrDefault(c => c.Category == groupNameById.GetValueOrDefault(inv.InvestmentGroupId.Value));

                        annualSavings = groupStats != null
                            ? groupStats.AnnualSavingsEur * share
                            : CombinedEnergySystemSavings() * share;
                        savingsSource = GetSavingsSource(category, inv);
                    }
                    else if (category == InvestmentCategory.Solar || category == InvestmentCategory.Storage)
                    {
                        // Solar and battery are treated as one integrated system.
                        // Prorate total system savings by this investment's share of combined net investment.
                        double totalSystemNet = investments
                            .Where(i => GetCategory(i) == InvestmentCategory.Solar || GetCategory(i) == InvestmentCategory.Storage)
                            .Sum(i => i.AmountEur - i.SubsidyEur);
                        double share = totalSystemNet > 0 ? (inv.AmountEur - inv.SubsidyEur) / totalSystemNet : 1.0;
                        annualSavings = CombinedEnergySystemSavings() * share;
                        savingsSource = "Energy system savings (solar + battery combined, prorated by investment share)";
                    }
                    else
                    {
                        annualSavings = CalculateCategoryAnnualSavings(
                            category, inv, monthlySolarSavings, monthlyArbitrageSavings);
                        savingsSource = GetSavingsSource(category, inv);
                    }

                    // Battery cycles: cyclesPerDay × days since installation.
                    // Simple and correct: each battery accumulates cycles since its install date.
                    double batteryCycles = category == InvestmentCategory.Storage
                        ? cyclesPerDay * (now - installDate).TotalDays
                        : 0.0;

                    return new InvestmentCategoryStats
                    {
                        Category = inv.Description,
                        TotalAmountEur = inv.AmountEur,
                        TotalSubsidyEur = inv.SubsidyEur,
                        ExpectedLifetimeYears = inv.ExpectedLifetimeYears,
                        InstallationDate = installDate,
                        MonthsSinceInstallation = monthsSince,
                        AnnualSavingsEur = annualSavings,
                        SavingsSource = savingsSource,
                        BatteryCycles = batteryCycles
                    };
                })
                .OrderByDescending(c => c.NetAmountEur)
                .ToList();

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
                CategoryBreakdown = categoryBreakdown,
                ComponentBreakdown = componentBreakdown
            };
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private double CalculateCategoryAnnualSavings(
            InvestmentCategory category,
            Investment representative,
            Dictionary<int, double> monthlySolarSavings,
            Dictionary<int, double> monthlyArbitrageSavings)
        {
            if (representative.EstimatedAnnualSavingsEur > 0)
                return representative.EstimatedAnnualSavingsEur;

            return category switch
            {
                InvestmentCategory.Solar => monthlySolarSavings.Values.Sum(),
                InvestmentCategory.Storage => monthlyArbitrageSavings.Values.Sum(),
                InvestmentCategory.HeatPump => _heatPumpConfig.IsConfigured
                    ? _heatPumpConfig.AnnualGasConsumptionM3 * _cachedEffectiveGasPrice
                      + _heatPumpConfig.GasStandingChargeEurPerYear
                      - _heatPumpConfig.AnnualElectricityCostEur
                    : 0.0,
                _ => 0.0
            };
        }

        private string GetSavingsSource(InvestmentCategory category, Investment representative)
        {
            if (representative.EstimatedAnnualSavingsEur > 0)
                return representative.SavingsDescription ?? "Manual estimate";

            return category switch
            {
                InvestmentCategory.Solar => "Energy system savings (solar + battery combined)",
                InvestmentCategory.Storage => "Energy system savings (solar + battery combined)",
                InvestmentCategory.HeatPump => _heatPumpConfig.IsConfigured
                    ? $"{_heatPumpConfig.AnnualGasConsumptionM3} m³ × €{_cachedEffectiveGasPrice:F4}/m³ + €{_heatPumpConfig.GasStandingChargeEurPerYear} vastrecht - {_heatPumpConfig.AnnualElectricityConsumptionKWh} kWh × €{_heatPumpConfig.EffectiveElectricityPriceEurPerKWh:F2}/kWh elektra"
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

            // Use cached effective gas price (historical average or live or configured fallback).
            double gasCostSaved = _heatPumpConfig.AnnualGasConsumptionM3 * _cachedEffectiveGasPrice;
            double annualSavings = gasCostSaved
                                 + _heatPumpConfig.GasStandingChargeEurPerYear
                                 - _heatPumpConfig.AnnualElectricityCostEur;

            return (annualSavings / 12.0) * months;
        }

        // Cached effective gas price — set by GetHeatPumpStatisticsAsync() before any calculation.
        private double _cachedEffectiveGasPrice;

        /// <summary>
        /// Returns fully resolved heat pump savings statistics.
        /// Resolves the effective gas price from the historical average in GasPrices table,
        /// falling back to the live Enever price or the configured value in appsettings.json.
        /// The view should display these values as-is, without any further calculation or interpretation.
        /// </summary>
        public async Task<HeatPumpStatistics> GetHeatPumpStatisticsAsync()
        {
            var (gasPrice, source) = await GetEffectiveGasPriceAsync();

            // Cache for use in synchronous callers (GetAnnualSavingsEur, CalculateHeatPumpSavings).
            _cachedEffectiveGasPrice = gasPrice;

            bool isHistoricalAverage = (await _gasPricesDataService.GetAllAsync()).Count > 0;

            // Resolve effective electricity price independently so this method is self-contained.
            // Uses the configured value when set; otherwise falls back to the measured average buy price.
            if (_heatPumpConfig.EffectiveElectricityPriceEurPerKWh == 0)
            {
                if (_heatPumpConfig.ElectricityPriceEurPerKWh > 0)
                {
                    _heatPumpConfig.EffectiveElectricityPriceEurPerKWh = _heatPumpConfig.ElectricityPriceEurPerKWh;
                }
                else
                {
                    var dataStart = await GetEarliestDataDateAsync();
                    var measurements = await GetMeasurementsAsync(dataStart, _timeZoneService.Now);
                    double avgBuyPrice = measurements.Any(m => m.GridImportWh > 0)
                        ? measurements.Where(m => m.GridImportWh > 0).Average(m => m.BuyingPriceEur)
                        : 0.25;
                    _heatPumpConfig.EffectiveElectricityPriceEurPerKWh = avgBuyPrice;
                }
            }

            double gasCostSaved = _heatPumpConfig.AnnualGasConsumptionM3 * gasPrice;
            double netSavings = gasCostSaved
                              + _heatPumpConfig.GasStandingChargeEurPerYear
                              - _heatPumpConfig.AnnualElectricityCostEur;

            return new HeatPumpStatistics
            {
                GasPriceEurPerM3 = gasPrice,
                IsLiveGasPrice = !isHistoricalAverage && _epexPricesService.CurrentGasPriceEurPerM3.HasValue,
                AnnualGasConsumptionM3 = _heatPumpConfig.AnnualGasConsumptionM3,
                AnnualGasCostSavedEur = gasCostSaved,
                GasStandingChargeEurPerYear = _heatPumpConfig.GasStandingChargeEurPerYear,
                AnnualElectricityConsumptionKWh = _heatPumpConfig.AnnualElectricityConsumptionKWh,
                EffectiveElectricityPriceEurPerKWh = _heatPumpConfig.EffectiveElectricityPriceEurPerKWh,
                AnnualElectricityCostEur = _heatPumpConfig.AnnualElectricityCostEur,
                NetAnnualSavingsEur = netSavings,
                GasPriceSource = source,
            };
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
                        .Where(m => m.BatteryPowerWatts > 0)
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
            // Arbitrage savings can only be measured from QuarterlyMeasurements —
            // EnergyHistory has no battery data. Starting from dataStart (which may
            // include the EnergyHistory period) would dilute the average with zero months.
            var firstMeasurement = await _measurementDataService.Get(async set =>
                await Task.FromResult(set.OrderBy(m => m.Time).FirstOrDefault()));

            var arbitrageStart = firstMeasurement?.Time ?? dataEnd;
            var current = new DateTime(arbitrageStart.Year, arbitrageStart.Month, 1);

            var allMonthly = new Dictionary<int, List<double>>();

            while (current <= dataEnd)
            {
                var clampedEnd = Math.Min(current.AddMonths(1).AddTicks(-1).Ticks, dataEnd.Ticks);
                var measurements = await GetMeasurementsAsync(current, new DateTime(clampedEnd));

                // Use BatteryPowerWatts directly — not BatteryMode — because in
                // ZeroNetHome mode the battery also discharges (positive watts) but
                // the mode is ZeroNetHome, not Discharging.
                double revenue = measurements
                    .Where(m => m.BatteryPowerWatts > 0)
                    .Sum(m => m.SellingPriceEur * m.BatteryDischargedKWh);

                double cost = measurements
                    .Where(m => m.BatteryPowerWatts < 0)
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

            // When less than 6 months of data available, simulate annual arbitrage
            // using historical EPEX prices and measured battery behaviour.
            if (allMonthly.Count < 6)
            {
                var simulated = await SimulateAnnualArbitrageAsync(arbitrageStart, dataEnd);
                if (simulated != null)
                    return simulated;
            }

            return result;
        }

        /// <summary>
        /// Simulates annual arbitrage savings using historical EPEX prices and
        /// measured battery behaviour (charge/discharge power and frequency).
        ///
        /// Strategy: each day, charge during the cheapest 25% of quarter-hours
        /// and discharge during the most expensive 25%, using the measured average
        /// power levels and round-trip efficiency from QuarterlyMeasurements.
        ///
        /// Returns a per-month savings dictionary, or null when insufficient data.
        /// </summary>
        private async Task<Dictionary<int, double>?> SimulateAnnualArbitrageAsync(
            DateTime measurementStart, DateTime dataEnd)
        {
            var measurements = await GetMeasurementsAsync(measurementStart, dataEnd);
            if (!measurements.Any()) return null;

            var discharging = measurements.Where(m => m.BatteryPowerWatts > 0).ToList();
            var charging = measurements.Where(m => m.BatteryPowerWatts < 0).ToList();

            if (!discharging.Any() || !charging.Any()) return null;

            double avgDischargeW = discharging.Average(m => m.BatteryPowerWatts);
            double avgChargeW = charging.Average(m => Math.Abs(m.BatteryPowerWatts));
            double totalDays = Math.Max(1, (dataEnd - measurementStart).TotalDays);
            double dischPerDay = discharging.Count / totalDays;
            double chargPerDay = charging.Count / totalDays;

            // Note: solar storage value is already included in BuildSeasonalSolarSavingsAsync
            // via self-consumption calculation. Only pure arbitrage (grid charge/discharge)
            // is calculated here to avoid double-counting.
            double avgBuyPrice = measurements.Any(m => m.BuyingPriceEur > 0)
                ? measurements.Where(m => m.BuyingPriceEur > 0).Average(m => m.BuyingPriceEur)
                : 0.25;

            // Load EPEX prices for arbitrage simulation.
            var epexFrom = dataEnd.AddYears(-1);
            var epexPrices = await _epexDataService.GetList(async set =>
            {
                var result = set
                    .Where(p => p.Time >= epexFrom && p.Time < dataEnd && p.Price.HasValue)
                    .OrderBy(p => p.Time)
                    .ToList();
                return await Task.FromResult(result);
            });

            if (!epexPrices.Any()) return null;

            var result = new Dictionary<int, double>();

            for (int month = 1; month <= 12; month++)
            {
                var monthPrices = epexPrices
                    .Where(p => p.Time.Month == month)
                    .Select(p => p.Price!.Value)
                    .OrderBy(p => p)
                    .ToList();

                if (!monthPrices.Any())
                {
                    result[month] = 0.0;
                    continue;
                }

                int n = monthPrices.Count;
                double avgCheap = monthPrices.Take(n / 4).Average();
                double avgExpensive = monthPrices.Skip(3 * n / 4).Average();

                int daysInMonth = DateTime.DaysInMonth(dataEnd.Year, month);
                double chargedKWh = chargPerDay * daysInMonth * avgChargeW * 0.25 / 1000.0;
                double dischargedKWh = dischPerDay * daysInMonth * avgDischargeW * 0.25 / 1000.0;

                double arbitrageRevenue = dischargedKWh * avgExpensive;
                double arbitrageCost = chargedKWh * Math.Max(0, avgCheap);

                result[month] = arbitrageRevenue - arbitrageCost;
            }

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
            // GlobalRadiation is measured per hour (W/m²) but stored per quarter-hour.
            // To avoid counting the same hour 4 times, use distinct hours only.
            double solarInstallationKWp = _powerSystemsConfig.Endpoints?
                .SelectMany(provider => provider.Value.Values)
                .Where(ep => ep.SolarPanels != null)
                .SelectMany(ep => ep.SolarPanels!.Values)
                .Sum(pv => pv.PeakPowerForArray) / 1000.0 ?? 0.0;

            // Sum distinct hourly radiation values to avoid 4x counting.
            double theoreticalKWh = measurements
                .GroupBy(m => new { m.Time.Date, m.Time.Hour })
                .Sum(g => g.First().GlobalRadiation / 1000.0 * solarInstallationKWp * 1.0); // × 1h

            stats.SolarPerformanceRatio = theoreticalKWh > 0
                ? stats.TotalSolarProductionKWh / theoreticalKWh
                : 0.0;
        }

        /// <summary>
        /// Self-consumed solar calculated per quarter-hour to avoid the circularity problem
        /// of totals (battery charges from solar then discharges to grid, making all export
        /// look like battery export and all solar look self-consumed).
        ///
        /// Per quarter: solar self-consumed = min(solar, consumption_from_own_sources)
        /// where consumption = grid_import + solar + battery_discharge - battery_charge - grid_export
        /// </summary>
        private static void CalculateSelfConsumedSolar(
            List<QuarterlyMeasurement> measurements, EnergyStatistics stats)
        {
            if (stats.TotalSolarProductionKWh <= 0)
            {
                stats.SelfConsumedSolarKWh = 0;
                return;
            }

            // Per quarter: how much solar went directly to household or battery (not to grid)?
            double selfConsumed = 0.0;
            foreach (var m in measurements)
            {
                if (m.SolarProductionKWh <= 0) continue;

                // Net household demand this quarter (positive = consuming, negative = surplus)
                double netDemand = m.GridImportKWh - m.GridExportKWh
                    + (m.BatteryPowerWatts < 0 ? Math.Abs(m.BatteryPowerWatts) * 0.25 / 1000.0 : 0)
                    - (m.BatteryPowerWatts > 0 ? m.BatteryPowerWatts * 0.25 / 1000.0 : 0);

                // Solar self-consumed = solar minus what went to the grid directly
                double solarToGrid = Math.Max(0, m.GridExportKWh - (m.BatteryPowerWatts > 0 ? m.BatteryPowerWatts * 0.25 / 1000.0 : 0));
                selfConsumed += Math.Max(0, m.SolarProductionKWh - solarToGrid);
            }

            stats.SelfConsumedSolarKWh = Math.Min(selfConsumed, stats.TotalSolarProductionKWh);
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

            stats.TotalBatteryChargedKWh = measurements.Sum(m => m.BatteryChargedKWh);
            stats.TotalBatteryDischargedKWh = measurements.Sum(m => m.BatteryDischargedKWh);

            // Reliable records only for round-trip efficiency.
            // Use all measurements — IsReliable filter already excludes unreliable periods.
            var reliable = measurements.Where(m => m.IsReliable).ToList();

            // Fall back to all measurements when no reliable records exist.
            if (!reliable.Any()) reliable = measurements;

            stats.ReliableBatteryChargedKWh = reliable.Sum(m => m.BatteryChargedKWh);
            stats.ReliableBatteryDischargedKWh = reliable.Sum(m => m.BatteryDischargedKWh);

            // Battery cycles: total charged kWh / capacity.
            // CyclesPerDay: use all days (not just complete) for a stable rate.
            // With few complete days the rate would be very noisy otherwise.
            int allDistinctDays = Math.Max(1, measurements
                .Select(m => m.Time.Date)
                .Distinct()
                .Count());

            double measuredCycles = BatteryCapacityKWh > 0
                ? stats.TotalBatteryChargedKWh / BatteryCapacityKWh
                : 0.0;

            stats.BatteryCycles = measuredCycles;
            stats.BatteryCyclesPerDay = measuredCycles / allDistinctDays;

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

            // Use BatteryPowerWatts directly — not BatteryMode — because in
            // ZeroNetHome mode the battery also charges/discharges but the mode
            // is ZeroNetHome, not Charging/Discharging.

            // Charging cost: energy bought from grid to charge batteries.
            double chargingCostEur = measurements
                .Where(m => m.BatteryPowerWatts < 0)
                .Sum(m => m.BuyingPriceEur * m.BatteryChargedKWh);

            // Import cost: energy bought for household consumption (excluding charging).
            double importCostEur = measurements
                .Sum(m => m.BuyingPriceEur * m.GridImportKWh) - chargingCostEur;

            // Export revenue: energy sold to grid.
            stats.GridExportRevenueEur = measurements
                .Where(m => m.BatteryPowerWatts > 0)
                .Sum(m => m.SellingPriceEur * m.BatteryDischargedKWh);

            stats.ActualEnergyCostEur = importCostEur + chargingCostEur - stats.GridExportRevenueEur;
            stats.ArbitrageProfitEur = stats.GridExportRevenueEur - chargingCostEur;

            // Planned arbitrage profit from MILP — sum of PlannedRevenueEur per quarter.
            stats.PlannedArbitrageProfitEur = measurements
                .Sum(m => m.PlannedRevenueEur);

            // Weighted average prices — weighted by actual energy flows, not quarter count.
            double totalImportKWh = measurements.Sum(m => m.GridImportKWh);
            double totalExportKWh = measurements.Sum(m => m.GridExportKWh);

            stats.WeightedAvgBuyPriceEurPerKWh = totalImportKWh > 0
                ? measurements.Where(m => m.GridImportWh > 0)
                    .Sum(m => m.BuyingPriceEur * m.GridImportKWh) / totalImportKWh
                : measurements.Average(m => m.BuyingPriceEur);

            stats.WeightedAvgSellPriceEurPerKWh = totalExportKWh > 0
                ? measurements.Where(m => m.GridExportWh > 0)
                    .Sum(m => m.SellingPriceEur * m.GridExportKWh) / totalExportKWh
                : measurements.Average(m => m.SellingPriceEur);

            stats.BaselineEnergyCostEur =
                stats.TotalConsumptionKWh * stats.WeightedAvgBuyPriceEurPerKWh;
        }

        // ── Data access ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns the single source of truth for the Energy Statistics dashboard.
        ///
        /// Calculation order (enforcing the design rules in DashboardStatistics):
        ///   1. Resolve heat pump electricity price (needs measurements)
        ///   2. Resolve gas price (needs Enever history)
        ///   3. Build seasonal savings averages (needs all historical measurements)
        ///   4. Build investment/ROI statistics (uses seasonal averages)
        ///   5. Build period statistics (uses selected period measurements only)
        ///   6. Assemble DashboardStatistics from steps 1–5
        ///
        /// Annual savings always come from seasonal averages (step 3).
        /// Period savings always come from period measurements (step 5).
        /// These two values are never mixed or interchanged.
        /// </summary>
        public async Task<DashboardStatistics> GetDashboardStatisticsAsync(DateTime start, DateTime end)
        {
            // ── Step 1: Clamp period ──────────────────────────────────────────
            if (_settingsConfig.StatisticsFromDate.HasValue)
            {
                var fromDate = _settingsConfig.StatisticsFromDate.Value;
                if (start == DateTime.MinValue || start < fromDate)
                    start = fromDate;
            }

            var now = _timeZoneService.Now;

            // ── Step 2: Resolve gas & electricity price ───────────────────────
            var heatPumpStats = await GetHeatPumpStatisticsAsync();

            // ── Step 3: Build seasonal savings averages ───────────────────────
            var dataStart = await GetEarliestDataDateAsync();
            var monthlySolarSavings = await BuildSeasonalSolarSavingsAsync(dataStart, now);
            var monthlyArbitrageSavings = await BuildSeasonalArbitrageSavingsAsync(dataStart, now);

            double energySystemAnnualSavings = monthlySolarSavings.Values.Sum()
                                             + monthlyArbitrageSavings.Values.Sum();
            double heatPumpAnnualSavings = heatPumpStats.NetAnnualSavingsEur;
            double totalAnnualSavings = energySystemAnnualSavings + heatPumpAnnualSavings;

            // ── Step 4: ROI statistics ────────────────────────────────────────
            var investments = await _investmentDataService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            var groups = await _groupService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            var groupNameById = groups.ToDictionary(g => g.Id, g => g.Name);
            var groupCategoryById = groups.ToDictionary(g => g.Id, g => g.Category);

            InvestmentCategory GetCategory(Investment inv) =>
                inv.InvestmentGroupId.HasValue && groupCategoryById.TryGetValue(inv.InvestmentGroupId.Value, out var cat)
                    ? cat : InvestmentCategory.Other;

            double totalNetInvestment = investments.Sum(i => i.AmountEur - i.SubsidyEur);
            var earliestPurchase = investments.Any() ? investments.Min(i => i.PurchaseDate) : now;

            // Realized savings: sum per component using seasonal averages prorated by months since install.
            double realizedSavings = investments.Sum(inv =>
            {
                var cat = GetCategory(inv);
                var monthsSince = (now.Year - inv.PurchaseDate.Year) * 12
                                + (now.Month - inv.PurchaseDate.Month);

                return cat switch
                {
                    InvestmentCategory.Solar or InvestmentCategory.Storage =>
                        energySystemAnnualSavings / 12.0 * monthsSince
                            * (totalNetInvestment > 0
                                ? (inv.AmountEur - inv.SubsidyEur) /
                                  investments.Where(i => GetCategory(i) == InvestmentCategory.Solar
                                                      || GetCategory(i) == InvestmentCategory.Storage)
                                             .Sum(i => i.AmountEur - i.SubsidyEur)
                                : 1.0),
                    InvestmentCategory.HeatPump =>
                        heatPumpAnnualSavings / 12.0 * monthsSince,
                    _ => 0.0
                };
            });

            // Projected savings: extrapolate from earliest purchase to now using seasonal averages.
            double monthsSinceEarliest = (now.Year - earliestPurchase.Year) * 12
                                        + (now.Month - earliestPurchase.Month);
            double projectedTotal = totalAnnualSavings / 12.0 * monthsSinceEarliest;

            // Break-even date.
            DateTime? breakEvenDate = totalAnnualSavings > 0
                ? earliestPurchase.AddDays(totalNetInvestment / totalAnnualSavings * 365.0)
                : null;

            // All measurements for cycle calculation.
            var allMeasurements = await GetMeasurementsAsync(dataStart, now);
            double totalChargedKWh = allMeasurements
                .Where(m => m.BatteryMode == SessyData.Model.BatteryMode.Charging && m.IsReliable)
                .Sum(m => Math.Abs(m.BatteryPowerWatts) * 0.25 / 1000.0);
            double measuredDays = allMeasurements.Any()
                ? (allMeasurements.Max(m => m.Time) - allMeasurements.Min(m => m.Time)).TotalDays
                : 1.0;
            double cyclesPerDay = BatteryCapacityKWh > 0 ? totalChargedKWh / BatteryCapacityKWh / measuredDays : 0.0;

            // Per-component breakdown.
            double totalSystemNet = investments
                .Where(i => GetCategory(i) == InvestmentCategory.Solar || GetCategory(i) == InvestmentCategory.Storage)
                .Sum(i => i.AmountEur - i.SubsidyEur);

            var componentBreakdown = investments.Select(inv =>
            {
                var cat = GetCategory(inv);
                var monthsSince = (now.Year - inv.PurchaseDate.Year) * 12
                                + (now.Month - inv.PurchaseDate.Month);

                double share = cat == InvestmentCategory.Solar || cat == InvestmentCategory.Storage
                    ? (totalSystemNet > 0 ? (inv.AmountEur - inv.SubsidyEur) / totalSystemNet : 1.0)
                    : 1.0;

                double annualSavings = cat switch
                {
                    InvestmentCategory.Solar or InvestmentCategory.Storage =>
                        energySystemAnnualSavings * share,
                    InvestmentCategory.HeatPump => heatPumpAnnualSavings,
                    _ => inv.EstimatedAnnualSavingsEur
                };

                string savingsSource = cat switch
                {
                    InvestmentCategory.Solar or InvestmentCategory.Storage =>
                        "Energy system savings (solar + battery combined, prorated by investment share)",
                    InvestmentCategory.HeatPump =>
                        $"950 m³ × €{_cachedEffectiveGasPrice:F4}/m³ + €{_heatPumpConfig.GasStandingChargeEurPerYear} vastrecht - {_heatPumpConfig.AnnualElectricityConsumptionKWh} kWh × €{_heatPumpConfig.EffectiveElectricityPriceEurPerKWh:F2}/kWh elektra",
                    _ => inv.SavingsDescription ?? "Manual estimate"
                };

                double batteryCycles = cat == InvestmentCategory.Storage
                    ? cyclesPerDay * (now - inv.PurchaseDate).TotalDays
                    : 0.0;

                return new InvestmentCategoryStats
                {
                    Category = inv.Description,
                    TotalAmountEur = inv.AmountEur,
                    TotalSubsidyEur = inv.SubsidyEur,
                    ExpectedLifetimeYears = inv.ExpectedLifetimeYears,
                    InstallationDate = inv.PurchaseDate,
                    MonthsSinceInstallation = monthsSince,
                    AnnualSavingsEur = annualSavings,
                    SavingsSource = savingsSource,
                    BatteryCycles = batteryCycles
                };
            }).OrderByDescending(c => c.NetAmountEur).ToList();

            // ── Step 5: Period measurements ───────────────────────────────────
            var periodStats = await GetEnergyStatisticsAsync(start, end);

            // Avg cycles per battery from component breakdown.
            var batteryCycleComponents = componentBreakdown.Where(c => c.BatteryCycles > 0).ToList();
            double avgCyclesPerBattery = batteryCycleComponents.Any()
                ? batteryCycleComponents.Average(c => c.BatteryCycles)
                : periodStats.BatteryCycles;

            // ── Step 6: Daily arbitrage trends ────────────────────────────────
            var arbitrageTrends = await GetDailyArbitrageTrendsAsync(start, end);

            // ── Step 7: Assemble DashboardStatistics ─────────────────────────
            return new DashboardStatistics
            {
                // ROI
                TotalNetInvestmentEur = totalNetInvestment,
                TotalRealizedSavingsEur = realizedSavings,
                ProjectedTotalSavingsEur = projectedTotal,
                TotalAnnualSavingsEur = totalAnnualSavings,
                EnergySystemAnnualSavingsEur = energySystemAnnualSavings,
                HeatPumpAnnualSavingsEur = heatPumpAnnualSavings,
                ProjectedBreakEvenDate = breakEvenDate,
                UsesProjection = dataStart > earliestPurchase,
                DataAvailableFrom = dataStart,
                ComponentBreakdown = componentBreakdown,

                // Period
                PeriodStart = periodStats.PeriodStart,
                PeriodEnd = periodStats.PeriodEnd,
                PeriodDays = periodStats.PeriodDays,
                PeriodSavingsEur = periodStats.TotalSavingsEur,
                TotalSolarProductionKWh = periodStats.TotalSolarProductionKWh,
                SelfSufficiencyPct = periodStats.SelfSufficiencyPct,
                SolarPerformanceRatio = periodStats.SolarPerformanceRatio,
                AvgDailySolarProductionKWh = periodStats.AvgDailySolarProductionKWh,
                PeakDailySolarProductionKWh = periodStats.PeakDailySolarProductionKWh,
                ArbitrageProfitEur = periodStats.ArbitrageProfitEur,
                PlannedArbitrageProfitEur = periodStats.PlannedArbitrageProfitEur,
                GridExportRevenueEur = periodStats.GridExportRevenueEur,
                TotalGridExportKWh = periodStats.TotalGridExportKWh,
                WeightedAvgBuyPriceEurPerKWh = periodStats.WeightedAvgBuyPriceEurPerKWh,
                WeightedAvgSellPriceEurPerKWh = periodStats.WeightedAvgSellPriceEurPerKWh,
                TotalBatteryChargedKWh = periodStats.TotalBatteryChargedKWh,
                TotalBatteryDischargedKWh = periodStats.TotalBatteryDischargedKWh,
                BatteryRoundTripEfficiencyPct = periodStats.BatteryRoundTripEfficiencyPct,
                BatteryCycles = periodStats.BatteryCycles,
                BatteryCyclesPerDay = periodStats.BatteryCyclesPerDay,
                AverageSocPct = periodStats.AverageSocPct,
                AvgCyclesPerBattery = avgCyclesPerBattery,
                TotalConsumptionKWh = periodStats.TotalConsumptionKWh,
                TotalGridImportKWh = periodStats.TotalGridImportKWh,
                AvgDailyConsumptionKWh = periodStats.AvgDailyConsumptionKWh,
                PeakDailyConsumptionKWh = periodStats.PeakDailyConsumptionKWh,
                WeekdayConsumptionKWh = periodStats.WeekdayConsumptionKWh,
                WeekendConsumptionKWh = periodStats.WeekendConsumptionKWh,

                // Heat pump
                HeatPumpIsConfigured = _heatPumpConfig.IsConfigured,
                GasPriceEurPerM3 = heatPumpStats.GasPriceEurPerM3,
                IsLiveGasPrice = heatPumpStats.IsLiveGasPrice,
                AnnualGasConsumptionM3 = heatPumpStats.AnnualGasConsumptionM3,
                AnnualGasCostSavedEur = heatPumpStats.AnnualGasCostSavedEur,
                GasStandingChargeEurPerYear = heatPumpStats.GasStandingChargeEurPerYear,
                AnnualElectricityConsumptionKWh = heatPumpStats.AnnualElectricityConsumptionKWh,
                EffectiveElectricityPriceEurPerKWh = heatPumpStats.EffectiveElectricityPriceEurPerKWh,
                AnnualElectricityCostEur = heatPumpStats.AnnualElectricityCostEur,
                GasPriceSource = heatPumpStats.GasPriceSource,

                // Charts
                DailyArbitrageTrends = arbitrageTrends,

                // Plan
                Plan = await _milpService.GetPlanStatisticsAsync(now, _hardwareStatusService.CurrentSocWh),
            };
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