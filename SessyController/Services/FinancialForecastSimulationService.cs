using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyController.Services.Optimization.Strategies;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>Stored outcome of the yearly replay (JSON next to the database).</summary>
    public sealed record FinancialForecastSimulation(DateTime CalculatedAt, int Days, FinancialForecast Forecast);

    /// <summary>
    /// Replays the last 12 full months through the real planner with next year's tariffs: measured
    /// house load, solar, temperature and market prices, one 48-hour solve per day (the first 24 hours
    /// kept), SOC carried from day to day. The grid flows that plan implies are priced like the other
    /// forecast columns. Runs once a day in the background, and at start-up when no result is stored;
    /// the Statistics page only reads the stored result.
    /// </summary>
    public class FinancialForecastSimulationService : BackgroundService
    {
        private const double QuarterHours = 0.25;

        private readonly IServiceScope _scope;
        private readonly LoggingService<FinancialForecastSimulationService> _logger;
        private readonly TimeZoneService _timeZoneService;
        private readonly SettingsService _settingsService;
        private readonly string _resultPath;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public FinancialForecastSimulation? Result { get; private set; }

        /// <summary>Days done in the running replay, or null when idle.</summary>
        public int? ProgressDays { get; private set; }

        public FinancialForecastSimulationService(
            IServiceScopeFactory scopeFactory,
            LoggingService<FinancialForecastSimulationService> logger,
            TimeZoneService timeZoneService,
            SettingsService settingsService,
            IConfiguration configuration)
        {
            _scope = scopeFactory.CreateScope();
            _logger = logger;
            _timeZoneService = timeZoneService;
            _settingsService = settingsService;
            _resultPath = ResultPath(configuration.GetConnectionString("SQLiteConnection"));
            Result = Load(_resultPath);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _settingsService.WaitForReadyAsync().ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Recalculate when nothing is stored yet or the stored result is from an earlier day.
                    if (Result == null || Result.CalculatedAt.Date < _timeZoneService.Now.Date)
                        await RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Financial forecast simulation failed: {ex.ToDetailedString()}");
                }
                finally
                {
                    ProgressDays = null;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var sp = _scope.ServiceProvider;
            var now = _timeZoneService.Now;
            var to = new DateTime(now.Year, now.Month, 1);
            var from = to.AddYears(-1);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── Tariffs of next year ──────────────────────────────────────────
            var taxes = (await sp.GetRequiredService<TaxesDataService>().GetList(async set =>
                    await Task.FromResult(set.Where(t => t.Time != null).ToList())).ConfigureAwait(false))
                .OrderBy(t => t.Time).ToList();
            var nextYearStart = new DateTime(now.Year + 1, 1, 1);
            var tx = FinancialForecastService.TaxesAt(taxes, nextYearStart);
            if (tx == null) return;

            // ── Measured inputs ───────────────────────────────────────────────
            var horizonEnd = to.AddDays(1);
            var prices = (await sp.GetRequiredService<EPEXPricesDataService>().GetList(async set =>
                    await Task.FromResult(set.Where(p => p.Time >= from && p.Time < horizonEnd && p.Price.HasValue).ToList())).ConfigureAwait(false))
                .GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.First().Price!.Value);

            var consumption = (await sp.GetRequiredService<ConsumptionDataService>().GetList(async set =>
                    await Task.FromResult(set.Where(c => c.Time >= from && c.Time < horizonEnd).ToList())).ConfigureAwait(false))
                .GroupBy(c => c.Time.DateFloorQuarter())
                .ToDictionary(g => g.Key, g => (Watts: g.Average(c => c.ConsumptionWh), TempC: g.Average(c => c.Temperature)));

            var solarKWh = (await sp.GetRequiredService<InverterMeasurementDataService>().GetList(async set =>
                    await Task.FromResult(set.Where(m => m.Time >= from && m.Time < horizonEnd).ToList())).ConfigureAwait(false))
                .GroupBy(m => m.Time.DateFloorQuarter())
                .ToDictionary(g => g.Key, g => g.Sum(m => m.SolarProductionKWh));

            // ── Battery as the planner sees it now ────────────────────────────
            var cfg = sp.GetRequiredService<IOptionsMonitor<SessyBatteryConfig>>().CurrentValue;
            var throttle = sp.GetRequiredService<ThrottleAnalysisService>();
            var efficiencyService = sp.GetRequiredService<BatteryEfficiencyService>();
            double capKWh = sp.GetRequiredService<BatteryContainer>().GetTotalCapacity() / 1000.0;
            if (capKWh <= 0.0) return;

            double maxChargeKW = cfg.TotalRawChargingCapacity / 1000.0;
            double maxDischargeKW = cfg.TotalRawDischargingCapacity / 1000.0;
            var (chargeEff, dischargeEff) = await efficiencyService.GetEfficienciesAsync().ConfigureAwait(false);
            var efficiency = await efficiencyService.GetEfficiencyCurveAsync().ConfigureAwait(false);
            var taper = await throttle.GetChargeTaperAsync().ConfigureAwait(false);
            var dischargeCapability = await throttle.GetDischargeCapabilityAsync(cfg.TotalRawDischargingCapacity).ConfigureAwait(false);
            var floor = await throttle.GetChargeCapabilityFloorAsync(cfg.TotalRawChargingCapacity).ConfigureAwait(false);
            var chargeCapability = await throttle.GetChargeCapabilityAsync(cfg.TotalRawChargingCapacity).ConfigureAwait(false);

            var settings = _settingsService.Current;
            double fallback = PowerLimits.FallbackRatio(settings.ThrottleFallbackPct);
            double baseChargeKW = PowerLimits.BaseChargeKW(maxChargeKW, taper, fallback);
            double baseDischargeKW = PowerLimits.BaseDischargeKW(maxDischargeKW, dischargeCapability, fallback);
            double minSocKWh = capKWh * Math.Clamp(settings.FixedNightReservePct, 0.0, 100.0) / 100.0;

            double replacementCost = await sp.GetRequiredService<ReplacementCostService>().GetReplacementCostAsync().ConfigureAwait(false);
            if (replacementCost <= 0.0)
                replacementCost = Math.Max(0.0, (await sp.GetRequiredService<ChargeCostBasisService>().GetSnapshotAsync().ConfigureAwait(false)).AverageCostBasisEur);

            var opt = new SessyOptions(
                QuarterMinutes: 15,
                CycleCostEurPerKWh: _settingsService.CycleCost,
                FutureValueDiscountPerHour: settings.FutureValueDiscountPerHour,
                ReservationPriceEurPerKWh: replacementCost,
                AllowCarryForward: settings.CarryForwardEnabled,
                AllowShift: settings.ShiftDischargeEnabled);

            IBatteryOptimizationStrategy strategy = settings.Strategy switch
            {
                OptimizationStrategy.SelfConsumption => new SelfConsumptionStrategy(),
                OptimizationStrategy.BatterySaving => new BatterySavingStrategy(),
                OptimizationStrategy.ProfitMaximization => new ProfitMaximizationStrategy(),
                _ => new BalancedStrategy()
            };

            double vat = 1.0 + tx.ValueAddedTax / 100.0;
            double socKWh = minSocKWh;
            var flows = new List<GridFlow>();
            int days = 0;

            for (var day = from; day < to; day = day.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();

                // 48-hour horizon like the live planner; only the first day is kept.
                var points = new List<PricePoint>();
                var netLoads = new List<double>();
                var marketPrices = new List<double>();

                for (var t = day; t < day.AddDays(2) && t < horizonEnd; t = t.AddMinutes(15))
                {
                    double? p = PriceAt(prices, t);
                    if (!p.HasValue) break;

                    // Missing load: the quarter is unknown, take the day's average rather than zero.
                    var load = consumption.TryGetValue(t, out var c) ? c : DayAverage(consumption, t);
                    double netLoadWh = load.Watts * QuarterHours - solarKWh.GetValueOrDefault(t) * 1000.0;

                    double buy = EnergyPriceCalculator.Calculate(p.Value, 0.0, tx.EnergyTax, tx.PurchaseCompensation, vat, buying: true, netting: tx.Netting);
                    double sell = EnergyPriceCalculator.Calculate(p.Value, 0.0, tx.EnergyTax, tx.SignedReturnDeliveryCompensation, vat, buying: false, netting: tx.Netting);

                    points.Add(new PricePoint(t, buy, sell, netLoadWh, netLoadWh < 0.0 ? -netLoadWh : 0.0,
                        MaxChargeKW: baseChargeKW < maxChargeKW ? baseChargeKW : null,
                        MaxDischargeKW: baseDischargeKW < maxDischargeKW ? baseDischargeKW : null,
                        TemperatureC: load.TempC, Temperature48hC: load.TempC));
                    netLoads.Add(netLoadWh);
                    marketPrices.Add(p.Value);
                }

                if (points.Count < 96) continue;   // incomplete day (missing prices)

                var spec = new BatterySpec(capKWh, socKWh, maxChargeKW, maxDischargeKW, chargeEff, dischargeEff,
                    ChargeTaper: taper, Efficiency: efficiency, DischargeCapability: dischargeCapability,
                    ChargeFloor: floor, ChargeCapability: chargeCapability);
                var bounds = points.Select(p => new SocBound(p.Start, minSocKWh, capKWh)).ToList();

                var result = await strategy.SolveAsync(new SolveContext(points, spec, opt, bounds)).ConfigureAwait(false);
                if (result == null || result.Plan.Count < 96) continue;

                for (int i = 0; i < 96; i++)
                {
                    var step = result.Plan[i];
                    double gridWh = netLoads[i] + (step.ChargeKW - step.DischargeKW) * QuarterHours * 1000.0;
                    flows.Add(new GridFlow(step.Start, Math.Max(0.0, gridWh) / 1000.0, Math.Max(0.0, -gridWh) / 1000.0, marketPrices[i]));
                }

                socKWh = Math.Clamp(result.Plan[95].SocEndKWh, 0.0, capKWh);
                days++;
                ProgressDays = days;

                // Let the live services breathe between days.
                await Task.Yield();
            }

            if (days == 0) return;

            var forecast = FinancialForecastService.Calculate($"{nextYearStart.Year}, planner replay",
                nextYearStart, nextYearStart.AddYears(1), flows, _ => tx);

            Result = new FinancialForecastSimulation(_timeZoneService.Now, days, forecast);
            Save(_resultPath, Result);

            _logger.LogWarning($"Financial forecast simulation: {days} days in {sw.Elapsed.TotalSeconds:F0} s, " +
                               $"import {forecast.ImportKWh:F0} kWh, export {forecast.ExportKWh:F0} kWh, total €{forecast.TotalEur:F0}.");
        }

        private static double? PriceAt(Dictionary<DateTime, double> prices, DateTime t)
        {
            if (prices.TryGetValue(t, out var p)) return p;
            if (prices.TryGetValue(new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0), out var ph)) return ph;
            return null;
        }

        private static (double Watts, double TempC) DayAverage(Dictionary<DateTime, (double Watts, double TempC)> consumption, DateTime t)
        {
            var sameDay = consumption.Where(kv => kv.Key.Date == t.Date).Select(kv => kv.Value).ToList();
            if (sameDay.Count == 0) return (consumption.Count > 0 ? consumption.Values.Average(v => v.Watts) : 0.0, ChargeTaper.RefTemperatureC);
            return (sameDay.Average(v => v.Watts), sameDay.Average(v => v.TempC));
        }

        /// <summary>Next to the SQLite file, so it survives a container update.</summary>
        private static string ResultPath(string? connectionString)
        {
            var dataSource = connectionString?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv[1].Trim())
                .FirstOrDefault();

            var directory = string.IsNullOrEmpty(dataSource) ? null : Path.GetDirectoryName(Path.GetFullPath(dataSource));
            return Path.Combine(directory ?? AppContext.BaseDirectory, "financial-forecast.json");
        }

        private FinancialForecastSimulation? Load(string path)
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<FinancialForecastSimulation>(File.ReadAllText(path))
                    : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not read {path}, recalculating: {ex.Message}");
                return null;
            }
        }

        private void Save(string path, FinancialForecastSimulation result)
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not write {path}: {ex.Message}");
            }
        }

        public override void Dispose()
        {
            _scope.Dispose();
            base.Dispose();
        }
    }
}
