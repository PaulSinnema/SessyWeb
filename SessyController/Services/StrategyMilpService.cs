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

#if DEBUG
        private const int MilpTimeLimitMs = 5000;
#else
        private const int MilpTimeLimitMs = 10000;
#endif

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
            ChargeCostBasisService chargeCostBasisService,
            ThrottleAnalysisService throttleAnalysisService,
            WeatherService weatherService)
            : base(logger, settingsService, sessyBatteryConfigMonitor, batteryContainer,
                   timeZoneService, taxesDataService, plannedActionDataService, plannedQuarterDataService,
                   chargeCostBasisService, throttleAnalysisService, weatherService)
        {
            _strategy = strategy;
        }

        protected override async Task<bool> BuildMilpPlanAsync(double socWh)
        {
            try
            {
                double capWh = _batteryContainer.GetTotalCapacity();
                double capKWh = capWh / 1000.0;
                double socKWh = socWh / 1000.0;

                double maxChargeKW = _settingsConfig.ChargingEfficiencyFactor > 0.0
                    ? _sessyBatteryConfig.TotalRawChargingCapacity / 1000.0 * _settingsConfig.ChargingEfficiencyFactor
                    : _sessyBatteryConfig.TotalChargingCapacity / 1000.0;

                double maxDischargeKW = _settingsConfig.DischargingEfficiencyFactor > 0.0
                    ? _sessyBatteryConfig.TotalRawDischargingCapacity / 1000.0 * _settingsConfig.DischargingEfficiencyFactor
                    : _sessyBatteryConfig.TotalDischargingCapacity / 1000.0;

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                // Known-price quarters always enter the solver. Predicted quarters are
                // included only when PredictedPriceMode is not Off; their prices are handled
                // below (risk margin in SoftMargin mode, as-is in Full mode).
                var quartersQuery = _quarterlyInfos
                    .Where(q => q.Time >= nowQuarter.AddMinutes(15));

                if (_settingsConfig.PredictedPriceMode == PredictedPriceMode.Off)
                    quartersQuery = quartersQuery.Where(q => !q.IsPriceExpected);

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

                _throttleRatioByTime.Clear();
                _chargeThrottleRatioByTime.Clear();

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
                        double chargeRatio = _throttleAnalysisService.GetChargeRatio(throttleBuckets, temp.Value);
                        double dischargeRatio = _throttleAnalysisService.GetDischargeRatio(throttleBuckets, temp.Value);
                        qMaxChargeKW = maxChargeKW * chargeRatio;
                        qMaxDischargeKW = maxDischargeKW * dischargeRatio;

                        // Store the direction-appropriate ratio so the throttle-free target
                        // power can be recovered later. Discharge ratio is used by default;
                        // the writeback picks the right one based on the executed mode.
                        _throttleRatioByTime[q.Time] = dischargeRatio;
                        _chargeThrottleRatioByTime[q.Time] = chargeRatio;
                    }

                    // Predicted quarters carry price uncertainty. In SoftMargin mode we make
                    // the solver conservative by widening their spread against arbitrage:
                    // raise the buy price and lower the sell price by the risk margin. Known
                    // quarters and Full mode use the prices unchanged.
                    double buyPrice = q.BuyingPrice;
                    double sellPrice = q.SellingPrice;
                    if (q.IsPriceExpected &&
                        _settingsConfig.PredictedPriceMode == PredictedPriceMode.SoftMargin)
                    {
                        double margin = _settingsConfig.PredictedPriceRiskMarginEur;
                        buyPrice += margin;
                        sellPrice = Math.Max(0.0, sellPrice - margin);
                    }

                    return new PricePoint(q.Time, buyPrice, sellPrice, q.NetLoadWh,
                        solarSurplusWh, qMaxChargeKW, qMaxDischargeKW);
                }).ToList();

                var socBounds = BuildSocBounds(quarters, socKWh, capKWh);

                var spec = new BatterySpec(
                    CapacityKWh: capKWh,
                    InitialSocKWh: socKWh,
                    MaxChargeKW: maxChargeKW,
                    MaxDischargeKW: maxDischargeKW,
                    ChargeEfficiency: 0.95,
                    DischargeEfficiency: 0.95);

                double beginSocCost = await _chargeCostBasisService
                    .GetAverageCostBasisEur().ConfigureAwait(false);

                // End-of-horizon value of stored energy. The FIFO cost basis reflects what
                // the energy *cost* (near 0 for solar), not what it is *worth*. Because the
                // solve horizon ends at the last known-price quarter, valuing leftover energy
                // at ~0 makes the solver see no reason to charge cheap now for the expensive
                // night/next day just beyond the horizon. Floor the value at the 25th
                // percentile (off-peak) buy price: high enough to stop the battery draining
                // to empty, but low enough that discharging into the expensive evening on the
                // last horizon day still beats hoarding. Using the median instead over-valued
                // held energy and suppressed evening discharge.
                double horizonFloorPrice = OffPeakBuyPrice(pricePoints);
                if (beginSocCost < horizonFloorPrice)
                    beginSocCost = horizonFloorPrice;

                var opt = new SessyOptions(
                    QuarterMinutes: 15,
                    CycleCostEurPerKWh: _settingsConfig.CycleCost,
                    TimeLimitMs: MilpTimeLimitMs,
                    BeginSocCostEurPerKWh: beginSocCost,
                    DischargeTimePreferenceFactor: _settingsConfig.DischargeTimePreferenceFactor);

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

        /// <summary>
        /// 25th-percentile (off-peak) buy price over the planning horizon. Used as a floor
        /// for the end-of-horizon value of stored energy: it reflects what holding energy is
        /// really worth (avoiding a cheap off-peak import), not the median — which is inflated
        /// by expensive evening peaks and would suppress evening discharge.
        /// </summary>
        private static double OffPeakBuyPrice(IReadOnlyList<PricePoint> pricePoints)
        {
            if (pricePoints == null || pricePoints.Count == 0)
                return 0.0;

            var sorted = pricePoints.Select(p => p.BuyEurPerKWh).OrderBy(v => v).ToList();
            int idx = sorted.Count / 4;
            return sorted[idx];
        }
    }
}