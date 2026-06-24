using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyController.Services.Optimization.Strategies;
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

                var quartersQuery = _quarterlyInfos
                    .Where(q => q.Time >= nowQuarter.AddMinutes(15))
                    .Where(q => !q.IsPriceExpected);

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

                    return new PricePoint(q.Time, q.BuyingPrice, q.SellingPrice, q.NetLoadWh,
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
    }
}