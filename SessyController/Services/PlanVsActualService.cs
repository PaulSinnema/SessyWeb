using Microsoft.EntityFrameworkCore;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Linq;

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
        private readonly BatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;

        public PlanVsActualService(
            PlannedQuarterDataService plannedQuarterDataService,
            ActualQuarterDataService actualQuarterDataService,
            BatteryContainer batteryContainer,
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
            var planned = await _plannedQuarterDataService.GetList(set => set.Where(item => item.Time >= from && item.Time <= to).ToListAsync());
            var actual = await _actualQuarterDataService.GetList(set => set.Where(item => item.Time >= from && item.Time <= to).ToListAsync());

            var actualByTime = actual.ToDictionary(a => a.Time);
            double capacityWh = _batteryContainer.GetTotalCapacity();

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
                CurtailmentQuarters = matched.Count(e => e.CurtailmentMode != "None" && !string.IsNullOrEmpty(e.CurtailmentMode)),
                From = from,
                To = to
            };
        }
    }

    public class PlanVsActualEntry
    {
        public DateTime Time { get; set; }

        // Planned
        public string PlannedMode { get; set; } = string.Empty;
        public double PlannedPowerW { get; set; }
        public double PlannedChargeLeftWh { get; set; }
        public double SellingPriceEurKWh { get; set; }
        public double BuyingPriceEurKWh { get; set; }
        public double SolarForecastW { get; set; }
        public double ConsumptionForecastW { get; set; }

        // Actual
        public string ActualMode { get; set; } = string.Empty;
        public double ActualPowerW { get; set; }
        public double ActualSocWh { get; set; }
        public string CurtailmentMode { get; set; } = string.Empty;
        public string StateMachineReason { get; set; } = string.Empty;

        // Derived
        public double SocDeviationPct { get; set; }
        public bool ModeMatch { get; set; }
    }

    public class PlanVsActualStats
    {
        public int QuarterCount { get; set; }
        public double AvgSocDeviationPct { get; set; }
        public double MaxSocDeviationPct { get; set; }
        public double ModeAccuracyPct { get; set; }
        public int CurtailmentQuarters { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
    }
}