using Microsoft.Extensions.Options;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
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
        private readonly EPEXPricesService _epexPricesService;

        // Total battery capacity in kWh for cycle calculation (3x Sessy 5.4 kWh).
        private const double BatteryCapacityKWh = 16.2;

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
                                       EPEXPricesService epexPricesService)
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
        }

        /// <summary>
        /// Returns the effective gas price in EUR/m³.
        /// Uses the live price from EpexPricesService when available;
        /// falls back to the configured value in appsettings.json.
        /// </summary>
        private double EffectiveGasPriceEurPerM3 =>
            _epexPricesService.CurrentGasPriceEurPerM3 ?? _heatPumpConfig.GasPriceEurPerM3;

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

                    if (isGroup && hasSolar && hasBattery)
                    {
                        double solarAnnual = monthlySolarSavings.Values.Sum();
                        double batteryAnnual = monthlyArbitrageSavings.Values.Sum();
                        annualSavings = solarAnnual + batteryAnnual;
                        savingsSource = "Export revenue + self-consumption + arbitrage (combined)";
                    }
                    else if (hasBattery)
                    {
                        double groupNetInvestment = g.Sum(i => i.AmountEur - i.SubsidyEur);
                        double share = totalBatteryNetInvestment > 0
                            ? groupNetInvestment / totalBatteryNetInvestment
                            : 1.0;
                        var effectiveArbitrage = monthlyArbitrageSavings
                            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value * share);
                        annualSavings = CalculateCategoryAnnualSavings(
                            category, first, monthlySolarSavings, effectiveArbitrage);
                        savingsSource = GetSavingsSource(category, first);
                    }
                    else
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
                        var groupKey = $"__group_{inv.InvestmentGroupId.Value}";
                        var groupStats = categoryBreakdown
                            .FirstOrDefault(c => c.Category == groupNameById.GetValueOrDefault(inv.InvestmentGroupId.Value));

                        annualSavings = groupStats != null
                            ? groupStats.AnnualSavingsEur * share
                            : CalculateCategoryAnnualSavings(category, inv, monthlySolarSavings, monthlyArbitrageSavings) * share;
                        savingsSource = GetSavingsSource(category, inv);
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
                InvestmentCategory.HeatPump => _heatPumpConfig.IsConfigured ? _heatPumpConfig.TotalAnnualSavingsEur : 0.0,
                _ => 0.0
            };
        }

        private string GetSavingsSource(InvestmentCategory category, Investment representative)
        {
            if (representative.EstimatedAnnualSavingsEur > 0)
                return representative.SavingsDescription ?? "Manual estimate";

            return category switch
            {
                InvestmentCategory.Solar => "Export revenue + self-consumption (measured)",
                InvestmentCategory.Storage => "Arbitrage profit (measured, prorated by investment share)",
                InvestmentCategory.HeatPump => _heatPumpConfig.IsConfigured
                    ? $"{_heatPumpConfig.AnnualGasConsumptionM3} m³ × €{EffectiveGasPriceEurPerM3:F4}/m³ + €{_heatPumpConfig.GasStandingChargeEurPerYear} vastrecht - {_heatPumpConfig.AnnualElectricityConsumptionKWh} kWh × €{_heatPumpConfig.EffectiveElectricityPriceEurPerKWh:F2}/kWh elektra"
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

            // Use live gas price when available; fall back to appsettings.json value.
            double gasCostSaved = _heatPumpConfig.AnnualGasConsumptionM3 * EffectiveGasPriceEurPerM3;
            double annualSavings = gasCostSaved
                                 + _heatPumpConfig.GasStandingChargeEurPerYear
                                 - _heatPumpConfig.AnnualElectricityCostEur;

            return (annualSavings / 12.0) * months;
        }

        /// <summary>
        /// Returns fully resolved heat pump savings statistics.
        /// The view should display these values as-is, without any further calculation or interpretation.
        /// </summary>
        public HeatPumpStatistics GetHeatPumpStatistics()
        {
            bool isLive = _epexPricesService.CurrentGasPriceEurPerM3.HasValue;
            double gasPrice = EffectiveGasPriceEurPerM3;
            double gasCostSaved = _heatPumpConfig.AnnualGasConsumptionM3 * gasPrice;
            double netSavings = gasCostSaved
                              + _heatPumpConfig.GasStandingChargeEurPerYear
                              - _heatPumpConfig.AnnualElectricityCostEur;

            string source = isLive
                ? $"Live TTF day-ahead via Enever.nl (configured fallback: € {_heatPumpConfig.GasPriceEurPerM3:F4}/m³)"
                : "Configured value (no live feed available — add Enever:Token to appsettings.json)";

            return new HeatPumpStatistics
            {
                GasPriceEurPerM3 = gasPrice,
                IsLiveGasPrice = isLive,
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