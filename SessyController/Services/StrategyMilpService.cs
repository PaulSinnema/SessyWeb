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
    /// <summary>
    /// Generic strategy-based MILP service.
    /// Eliminates code duplication: all concrete strategy services share
    /// the same BuildMilpPlanAsync implementation, differing only in strategy.
    /// </summary>
    public abstract class StrategyMilpService : MilpServiceBase
    {
        private readonly IBatteryOptimizationStrategy _strategy;
        private readonly BatteryEfficiencyService _batteryEfficiencyService;

        protected StrategyMilpService(
            IBatteryOptimizationStrategy strategy,
            LoggingService<MilpServiceBase> logger,
            SettingsService settingsService,
            IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
            BatteryContainer batteryContainer,
            TimeZoneService timeZoneService,
            TaxesDataService taxesDataService,
            PlannedActionDataService plannedActionDataService,
            PlannedQuarterDataService plannedQuarterDataService,
            ForecastSnapshotDataService forecastSnapshotDataService,
            ChargeCostBasisService chargeCostBasisService,
            ReplacementCostService replacementCostService,
            ThrottleAnalysisService throttleAnalysisService,
            WeatherService weatherService,
            BatteryEfficiencyService batteryEfficiencyService,
            ControlModeService controlMode)
            : base(logger, settingsService, sessyBatteryConfigMonitor, batteryContainer,
                   timeZoneService, taxesDataService, plannedActionDataService, plannedQuarterDataService,
                   forecastSnapshotDataService, chargeCostBasisService, replacementCostService,
                   throttleAnalysisService, weatherService, controlMode)
        {
            _strategy = strategy;
            _batteryEfficiencyService = batteryEfficiencyService;
        }

        protected override async Task<bool> BuildMilpPlanAsync(double socWh)
        {
            try
            {
                double capWh = _batteryContainer.GetTotalCapacity();
                double capKWh = capWh / 1000.0;
                double socKWh = socWh / 1000.0;

                // Nameplate power. The only derate applied is the throttle ratio below, which is
                // measured per temperature bucket. A second, manually configured derate on top of
                // it would count the same effect twice.
                double maxChargeKW = _sessyBatteryConfig.TotalRawChargingCapacity / 1000.0;
                double maxDischargeKW = _sessyBatteryConfig.TotalRawDischargingCapacity / 1000.0;

                // Energy efficiency is a different quantity from the power throttle: it decides
                // how much of the stored energy comes back out, and therefore whether arbitrage
                // pays. Derived from measurements, with a configured fallback.
                var (chargeEfficiency, dischargeEfficiency) = await _batteryEfficiencyService
                    .GetEfficienciesAsync().ConfigureAwait(false);

                // ... and how much of it survives depends on the power it moves at, because a large
                // part of the conversion loss is fixed overhead. Without this the planner sees no
                // reason to concentrate energy in fewer, fuller quarters.
                var efficiencyCurve = await _batteryEfficiencyService
                    .GetEfficiencyCurveAsync().ConfigureAwait(false);

                if (efficiencyCurve.Samples > 0 && efficiencyCurve.ChargeOverheadKW > 0.0)
                    _logger.LogInformation(
                        $"Efficiency curve fitted on {efficiencyCurve.Samples} samples: " +
                        $"charge {efficiencyCurve.ChargeCeiling:F3} - {efficiencyCurve.ChargeOverheadKW:F3}/kW " +
                        $"({efficiencyCurve.ChargeAt(1.0):F2} at 1 kW, {efficiencyCurve.ChargeAt(5.0):F2} at 5 kW), " +
                        $"discharge {efficiencyCurve.DischargeCeiling:F3} - {efficiencyCurve.DischargeOverheadKW:F3}/kW.");

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                // Known-price quarters always enter the solver. Predicted quarters are
                // always included too, so the horizon reaches into the coming night and the
                // solver keeps enough charge for it instead of guessing an end-of-horizon
                // value. How predicted prices are treated depends on the mode:
                //   Off        → reserve-only: extend the horizon for night coverage, but no
                //                trading on the uncertain prices (no charge, no export).
                //   SoftMargin → traded with a risk margin (applied below).
                //   Full       → traded as-is.
                //
                // Includes the current quarter (nowQuarter), not just future ones: it starts
                // from the real, current SOC, so a decision already baked into the in-progress
                // quarter by an earlier (possibly wrong) solve can still be corrected for
                // whatever time remains in it, instead of being locked in until the next quarter.
                var quartersQuery = _quarterlyInfos
                    .Where(q => q.Time >= nowQuarter);

                // Optional planning horizon limit: ignore quarters beyond N hours so the
                // solver cannot defer discharge to a far-future peak.
                if (_settingsConfig.PlanningHorizonHours > 0)
                {
                    var horizonEnd = nowQuarter.AddHours(_settingsConfig.PlanningHorizonHours);
                    quartersQuery = quartersQuery.Where(q => q.Time < horizonEnd);
                }

                var quarters = quartersQuery
                    .OrderBy(q => q.Time)
                    .ToList();

                if (quarters.Count == 0) return false;

                // Load throttle ratios once; they are keyed on temperature, not date, so the
                // same bucket applies to any quarter at that temperature.
                var throttleBuckets = await _throttleAnalysisService.GetThrottleBucketsAsync()
                    .ConfigureAwait(false);

                // Charge power is limited far more by the CC/CV taper than by temperature, so the
                // taper — when measured — replaces the temperature ratio on the charge side.
                // Applying both would count the same throttle twice.
                var chargeTaper = await _throttleAnalysisService.GetChargeTaperAsync()
                    .ConfigureAwait(false);
                _chargeTaper = chargeTaper;

                if (chargeTaper.Samples > 0)
                    _logger.LogInformation(
                        $"Charge taper fitted on {chargeTaper.Samples} envelope points: " +
                        $"ratio = {chargeTaper.A:F3} - {chargeTaper.B:F3} * soc " +
                        $"- {chargeTaper.C:F4} * (temp - {ChargeTaper.RefTemperatureC:F0}) " +
                        $"- {chargeTaper.D:F4} * (t48 - temp) " +
                        $"({chargeTaper.Ratio(0.2):F2} at 20% SOC, {chargeTaper.Ratio(0.8):F2} at 80%, " +
                        $"{chargeTaper.Ratio(0.5, 30.0, 28.0):F2} at 50% SOC in a heatwave).");

                // Temperatures for the heat build-up term: measured history for the part of the
                // 48-hour window that lies in the past, forecast for the rest.
                var temperatureByHour = await BuildTemperatureSeriesAsync(quarters, nowQuarter)
                    .ConfigureAwait(false);

                _throttleRatioByTime.Clear();
                _taperInputsByTime.Clear();

                double throttleFallback = _settingsConfig.ThrottleFallbackPct > 0.0
                    ? _settingsConfig.ThrottleFallbackPct / 100.0
                    : 0.80;

                var pricePoints = quarters.Select(q =>
                {
                    double solarSurplusWh = q.NetLoadWh < 0.0 ? -q.NetLoadWh : 0.0;

                    // Reduce the per-quarter power caps by the throttle expected at the
                    // forecast outside temperature. No temperature or no data → full power.
                    double? qMaxChargeKW = null;
                    double? qMaxDischargeKW = null;

                    var temp = _weatherService.GetTemperature(q.Time);
                    if (temp.HasValue)
                    {
                        // Discharge keeps the temperature buckets: the SOC signal is weak there.
                        // Fall back to the configured estimate when this temperature has no
                        // samples — assuming no throttle at all would make the planner request
                        // power the battery cannot deliver.
                        if (!_throttleAnalysisService.TryGetDischargeRatio(throttleBuckets, temp.Value, out double dischargeRatio))
                            dischargeRatio = throttleFallback;

                        qMaxDischargeKW = maxDischargeKW * dischargeRatio;
                        _throttleRatioByTime[q.Time] = dischargeRatio;

                        // Charge side: with a measured taper the planner derates per quarter from
                        // the SOC and temperatures it has there, so no cap is imposed here — a
                        // temperature ratio on top would derate twice. Without a taper the old
                        // bucket ratio still applies.
                        if (chargeTaper.Samples == 0)
                        {
                            if (!_throttleAnalysisService.TryGetChargeRatio(throttleBuckets, temp.Value, out double chargeRatio))
                                chargeRatio = throttleFallback;

                            qMaxChargeKW = maxChargeKW * chargeRatio;
                        }
                    }

                    double? quarterTemp = temp ?? Mean(temperatureByHour, q.Time, q.Time);
                    double? mean48h = Mean(temperatureByHour, q.Time.AddHours(-Mean48hHours), q.Time);

                    if (quarterTemp.HasValue)
                        _taperInputsByTime[q.Time] = (quarterTemp.Value, mean48h ?? quarterTemp.Value);

                    // Predicted quarters carry price uncertainty.
                    //   Off        → reserve-only (no trading; horizon extension only).
                    //   SoftMargin → widen the spread by the risk margin so only ample
                    //                spreads are traded.
                    //   Full       → prices as-is.
                    double buyPrice = q.BuyingPrice;
                    double sellPrice = q.SellingPrice;
                    bool reserveOnly = false;

                    if (q.IsPriceExpected)
                    {
                        switch (_settingsConfig.PredictedPriceMode)
                        {
                            case PredictedPriceMode.Off:
                                reserveOnly = true;
                                break;
                            case PredictedPriceMode.SoftMargin:
                                double margin = _settingsConfig.PredictedPriceRiskMarginEur;
                                buyPrice += margin;
                                sellPrice = Math.Max(0.0, sellPrice - margin);
                                break;
                            case PredictedPriceMode.Full:
                                break;
                        }
                    }

                    return new PricePoint(q.Time, buyPrice, sellPrice, q.NetLoadWh,
                        solarSurplusWh, qMaxChargeKW, qMaxDischargeKW, reserveOnly,
                        quarterTemp, mean48h);
                }).ToList();

                var socBounds = BuildSocBounds(quarters, socKWh, capKWh);

                var spec = new BatterySpec(
                    CapacityKWh: capKWh,
                    InitialSocKWh: socKWh,
                    MaxChargeKW: maxChargeKW,
                    MaxDischargeKW: maxDischargeKW,
                    ChargeEfficiency: chargeEfficiency,
                    DischargeEfficiency: dischargeEfficiency,
                    ChargeTaper: chargeTaper,
                    Efficiency: efficiencyCurve);

                // What replacing a kWh will cost, measured over a trailing window. It prices both
                // the floor under selling stock and the option of keeping energy past the end of
                // the horizon. Until there is enough price history it is 0, and the FIFO cost
                // basis stands in as the floor — what was paid is the best guess left. Solar
                // layers cost 0 there, so a solar-filled battery behaves as it always did.
                double replacementCost = await _replacementCostService.GetReplacementCostAsync()
                    .ConfigureAwait(false);

                if (replacementCost <= 0.0)
                {
                    var costBasis = await _chargeCostBasisService.GetSnapshotAsync().ConfigureAwait(false);
                    replacementCost = Math.Max(0.0, costBasis.AverageCostBasisEur);
                }

                var opt = new SessyOptions(
                    QuarterMinutes: 15,
                    CycleCostEurPerKWh: _settingsService.CycleCost,
                    FutureValueDiscountPerHour: _settingsConfig.FutureValueDiscountPerHour,
                    ReplacementCostEurPerKWh: replacementCost,
                    AllowCarryForward: _settingsConfig.CarryForwardEnabled);

                var context = new SolveContext(pricePoints, spec, opt, socBounds);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await _strategy.SolveAsync(context).ConfigureAwait(false);
                sw.Stop();

                return ApplySolveResult(result, sw.ElapsedMilliseconds, quarters.Count, socKWh);
            }
            catch (Exception ex)
            {
                _logger.LogError($"{GetType().Name}.BuildMilpPlanAsync failed: {ex.ToDetailedString()}");
                return false;
            }
        }

        /// <summary>Length of the heat build-up window used by the charge taper.</summary>
        private static int Mean48hHours => ThrottleAnalysisService.Mean48hWindowHours;

        /// <summary>
        /// One temperature per hour covering the 48 hours before the horizon plus the horizon
        /// itself. Measured values are used where they exist; the hourly forecast fills the rest,
        /// so a quarter halfway through the horizon still gets a 48-hour mean that blends real
        /// history with prediction.
        /// </summary>
        private async Task<SortedList<DateTime, double>> BuildTemperatureSeriesAsync(
            IReadOnlyList<QuarterlyInfo> quarters, DateTime nowQuarter)
        {
            var series = new SortedList<DateTime, double>();

            var historyStart = nowQuarter.AddHours(-Mean48hHours);
            var measured = await _throttleAnalysisService
                .GetMeasuredTemperaturesAsync(historyStart, nowQuarter)
                .ConfigureAwait(false);

            foreach (var (time, temperature) in measured)
                series[time.DateHour()] = temperature;

            // Forecast for everything the measurements do not cover, history included: a gap in
            // the measured series would otherwise skew the mean.
            var last = quarters.Count > 0 ? quarters[^1].Time : nowQuarter;

            for (var hour = historyStart.DateHour(); hour <= last.DateHour(); hour = hour.AddHours(1))
            {
                if (series.ContainsKey(hour)) continue;

                var forecast = _weatherService.GetTemperature(hour);
                if (forecast.HasValue) series[hour] = forecast.Value;
            }

            return series;
        }

        /// <summary>
        /// Mean of the series over [from, to]. Null when the window holds no data at all, so the
        /// caller can leave the temperature unknown rather than pass a made-up number.
        /// </summary>
        private static double? Mean(SortedList<DateTime, double> series, DateTime from, DateTime to)
        {
            double sum = 0.0;
            int count = 0;

            foreach (var entry in series)
            {
                if (entry.Key < from.DateHour()) continue;
                if (entry.Key > to.DateHour()) break;

                sum += entry.Value;
                count++;
            }

            return count == 0 ? null : sum / count;
        }
    }
}