using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services.Statistics
{
    public sealed record BatteryMonth(DateTime Month, double ChargedKWh, double DischargedKWh, double? RoundTripPercent, double Cycles, double CumulativeCycles)
    {
        public string Label => Month.ToString("MMM yyyy");
    }

    public sealed record BatteryStatisticsResult(
        List<BatteryMonth> Months,
        double CapacityKWh,
        double TotalCycles,
        int ExpectedCycles,
        double CyclesPerYear,
        DateTime? FirstDay)
    {
        /// <summary>Years until the expected cycles are used at the current pace; null when unknown.</summary>
        public double? YearsLeft => ExpectedCycles > 0 && CyclesPerYear > 0.0
            ? Math.Max(0.0, ExpectedCycles - TotalCycles) / CyclesPerYear
            : null;
    }

    /// <summary>Battery use per month: energy in and out, round-trip efficiency and full-cycle equivalents.</summary>
    public class BatteryStatisticsService
    {
        private readonly QuarterlyMeasurementDataService _measurementDataService;
        private readonly InvestmentDataService _investmentDataService;
        private readonly InvestmentGroupDataService _investmentGroupDataService;
        private readonly IBatteryContainer _batteryContainer;

        public BatteryStatisticsService(QuarterlyMeasurementDataService measurementDataService,
                                        InvestmentDataService investmentDataService,
                                        InvestmentGroupDataService investmentGroupDataService,
                                        IBatteryContainer batteryContainer)
        {
            _measurementDataService = measurementDataService;
            _investmentDataService = investmentDataService;
            _investmentGroupDataService = investmentGroupDataService;
            _batteryContainer = batteryContainer;
        }

        public async Task<BatteryStatisticsResult> GetAsync()
        {
            var measurements = await _measurementDataService.GetList(async set =>
                await Task.FromResult(set.Where(m => m.IsReliable).OrderBy(m => m.Time).ToList()));

            var groups = await _investmentGroupDataService.GetList(set => Task.FromResult(set.ToList()));
            var storageGroupIds = groups.Where(g => g.Category == InvestmentCategory.Storage).Select(g => g.Id).ToHashSet();

            var batteries = (await _investmentDataService.GetList(set => Task.FromResult(set.ToList())))
                .Where(i => i.InvestmentGroupId.HasValue && storageGroupIds.Contains(i.InvestmentGroupId.Value) && i.CapacityWh > 0.0)
                .ToList();

            // Configured capacity first; the investments are the fallback.
            var capacityKWh = _batteryContainer.GetTotalCapacity() / 1000.0;
            if (capacityKWh <= 0.0)
                capacityKWh = batteries.Sum(b => b.CapacityWh) / 1000.0;

            return Build(measurements, capacityKWh, ExpectedCycles(batteries));
        }

        /// <summary>Capacity-weighted expected cycles of the battery investments; 0 when none are entered.</summary>
        public static int ExpectedCycles(IReadOnlyCollection<Investment> batteries)
        {
            var withCycles = batteries.Where(b => b.ExpectedTotalCycles > 0 && b.CapacityWh > 0.0).ToList();
            var capacity = withCycles.Sum(b => b.CapacityWh);

            return capacity > 0.0 ? (int)Math.Round(withCycles.Sum(b => b.CapacityWh * b.ExpectedTotalCycles) / capacity) : 0;
        }

        public static BatteryStatisticsResult Build(IReadOnlyList<QuarterlyMeasurement> measurements, double capacityKWh, int expectedCycles)
        {
            var months = new List<BatteryMonth>();
            var cumulative = 0.0;

            foreach (var month in measurements.GroupBy(m => new DateTime(m.Time.Year, m.Time.Month, 1)).OrderBy(g => g.Key))
            {
                var list = month.ToList();
                var charged = list.Sum(m => m.BatteryChargedKWh);
                var discharged = list.Sum(m => m.BatteryDischargedKWh);
                var roundTrip = BatteryEfficiencyService.RoundTrip(list);
                var cycles = capacityKWh > 0.0 ? discharged / capacityKWh : 0.0;

                cumulative += cycles;
                months.Add(new BatteryMonth(month.Key, charged, discharged, roundTrip * 100.0, cycles, cumulative));
            }

            DateTime? first = measurements.Count > 0 ? measurements[0].Time.Date : null;
            var years = measurements.Count > 1 ? (measurements[^1].Time - measurements[0].Time).TotalDays / 365.25 : 0.0;

            return new BatteryStatisticsResult(months, capacityKWh, cumulative, expectedCycles,
                years >= 0.1 ? cumulative / years : 0.0, first);
        }
    }
}
