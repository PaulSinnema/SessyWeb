using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Calculates plan vs actual statistics by joining PlannedQuarter and ActualQuarter data.
    /// Answers the question: "How well did the planner predict reality?"
    /// </summary>
    public class PlanVsActualService
    {
        private readonly PlannedQuarterDataService _plannedQuarterDataService;
        private readonly ActualQuarterDataService _actualQuarterDataService;
        private readonly IBatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;

        public PlanVsActualService(
            PlannedQuarterDataService plannedQuarterDataService,
            ActualQuarterDataService actualQuarterDataService,
            IBatteryContainer batteryContainer,
            TimeZoneService timeZoneService)
        {
            _plannedQuarterDataService = plannedQuarterDataService;
            _actualQuarterDataService = actualQuarterDataService;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// Returns plan vs actual comparison for the given time range.
        /// </summary>
        public async Task<List<PlanVsActualEntry>> GetAsync(DateTime from, DateTime to)
        {
            var planned = await _plannedQuarterDataService.GetList(set =>
                Task.FromResult(set.Where(item => item.Time >= from && item.Time <= to)
                    .OrderBy(item => item.Time).ToList()));

            var actual = await _actualQuarterDataService.GetList(set =>
                Task.FromResult(set.Where(item => item.Time >= from && item.Time <= to)
                    .OrderBy(item => item.Time).ToList()));

            var actualByTime = actual.ToDictionary(a => a.Time);
            double capacityWh = _batteryContainer?.GetTotalCapacity() ?? 0.0;

            return planned
                .Select(p =>
                {
                    actualByTime.TryGetValue(p.Time, out var a);

                    double socDeviationPct = 0.0;
                    if (a != null && capacityWh > 0 && p.PlannedChargeLeftWh > 0)
                        socDeviationPct = Math.Abs(a.ActualSocWh - p.PlannedChargeLeftWh)
                                          / capacityWh * 100.0;

                    return new PlanVsActualEntry
                    {
                        Time = p.Time,
                        PlannedMode = p.PlannedMode,
                        PlannedPowerW = p.PlannedPowerW,
                        PlannedChargeLeftWh = p.PlannedChargeLeftWh,
                        SellingPriceEurKWh = p.SellingPriceEurKWh,
                        BuyingPriceEurKWh = p.BuyingPriceEurKWh,
                        SolarForecastW = p.SolarForecastW,
                        ConsumptionForecastW = p.ConsumptionForecastW,
                        ActualMode = a?.ActualMode ?? string.Empty,
                        ActualPowerW = a?.ActualPowerW ?? 0.0,
                        ActualSocWh = a?.ActualSocWh ?? 0.0,
                        CurtailmentMode = a?.CurtailmentMode ?? string.Empty,
                        StateMachineReason = a?.StateMachineReason ?? string.Empty,
                        SocDeviationPct = socDeviationPct,
                        ModeMatch = a != null && a.ActualMode == p.PlannedMode
                    };
                })
                .OrderBy(e => e.Time)
                .ToList();
        }

        /// <summary>
        /// Returns aggregate statistics for the given time range.
        /// </summary>
        public async Task<PlanVsActualStats> GetStatsAsync(DateTime from, DateTime to)
        {
            var entries = await GetAsync(from, to).ConfigureAwait(false);
            var matched = entries.Where(e => !string.IsNullOrEmpty(e.ActualMode)).ToList();

            if (!matched.Any())
                return new PlanVsActualStats();

            return new PlanVsActualStats
            {
                QuarterCount = matched.Count,
                AvgSocDeviationPct = matched.Average(e => e.SocDeviationPct),
                MaxSocDeviationPct = matched.Max(e => e.SocDeviationPct),
                ModeAccuracyPct = matched.Count(e => e.ModeMatch) * 100.0 / matched.Count,
                CurtailmentQuarters = matched.Count(e =>
                    e.CurtailmentMode != "None" && !string.IsNullOrEmpty(e.CurtailmentMode)),
                From = from,
                To = to
            };
        }
    }
}