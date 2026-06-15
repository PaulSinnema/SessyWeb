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
            ChargeCostBasisService chargeCostBasisService)
            : base(logger, settingsService, sessyBatteryConfigMonitor, batteryContainer,
                   timeZoneService, taxesDataService, plannedActionDataService, plannedQuarterDataService,
                   chargeCostBasisService)
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

                var pricePoints = quarters.Select(q =>
                {
                    double solarSurplusWh = q.NetLoadWh < 0.0 ? -q.NetLoadWh : 0.0;
                    return new PricePoint(q.Time, q.BuyingPrice, q.SellingPrice, q.NetLoadWh, solarSurplusWh);
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