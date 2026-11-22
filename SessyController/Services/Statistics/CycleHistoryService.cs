using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using SessyCommon;

namespace SessyController.Services.Statistics
{
    /// <summary>Full-cycle equivalents of one month, split by where the discharged energy comes from.</summary>
    public sealed record CycleMonth(DateTime Month, double MeasuredCycles, double ReconstructedCycles, double EstimatedCycles,
                                    double CumulativeCycles, double CumulativeCyclesWithoutEstimate)
    {
        public string Label => Month.ToString("MMM yyyy");

        public double KnownCycles => MeasuredCycles + ReconstructedCycles;
    }

    /// <summary>The cycle history of the whole bank or of one battery.</summary>
    public sealed record CycleSeries(string Name, double CapacityKWh, DateTime? Start, List<CycleMonth> Months)
    {
        public double TotalCycles(bool withEstimate) =>
            Months.Sum(m => m.KnownCycles + (withEstimate ? m.EstimatedCycles : 0.0));
    }

    public sealed record CycleHistoryResult(CycleSeries Total, List<CycleSeries> Batteries)
    {
        /// <summary>Average lifetime cycles over the batteries.</summary>
        public double AveragePerBattery(bool withEstimate) =>
            Batteries.Count > 0 ? Batteries.Average(b => b.TotalCycles(withEstimate)) : Total.TotalCycles(withEstimate);

        /// <summary>The battery matching a name ("Sessy 2" also matches "Battery 2"); null when none does.</summary>
        public CycleSeries? Find(string? name) =>
            Batteries.FirstOrDefault(b => string.Equals(b.Name.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? Batteries.FirstOrDefault(b => CycleHistoryService.Number(name) is string n && n == CycleHistoryService.Number(b.Name));
    }

    /// <summary>One battery as the history sees it: capacity and the first quarter it was in use.</summary>
    public sealed record CycleBattery(string Id, string Name, double CapacityKWh, DateTime ActiveFrom);

    /// <summary>
    /// Full cycles since the batteries were bought. Per quarter the discharged energy is taken from the best
    /// source there is: measured (QuarterlyMeasurements), reconstructed (consumption − solar − grid, where the
    /// battery is the remainder of the energy balance) or estimated (the measured average cycles per quarter).
    /// Per battery only its own readings count as measured; before those, its cycles follow the bank's.
    /// </summary>
    public class CycleHistoryService
    {
        private readonly QuarterlyMeasurementDataService _measurementDataService;
        private readonly BatteryQuarterMeasurementDataService _batteryMeasurementDataService;
        private readonly EnergyHistoryDataService _energyHistoryDataService;
        private readonly QuarterlyFactsService _quarterlyFactsService;
        private readonly InvestmentDataService _investmentDataService;
        private readonly InvestmentGroupDataService _investmentGroupDataService;
        private readonly BatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;

        public CycleHistoryService(QuarterlyMeasurementDataService measurementDataService,
                                   BatteryQuarterMeasurementDataService batteryMeasurementDataService,
                                   EnergyHistoryDataService energyHistoryDataService,
                                   QuarterlyFactsService quarterlyFactsService,
                                   InvestmentDataService investmentDataService,
                                   InvestmentGroupDataService investmentGroupDataService,
                                   BatteryContainer batteryContainer,
                                   TimeZoneService timeZoneService)
        {
            _measurementDataService = measurementDataService;
            _batteryMeasurementDataService = batteryMeasurementDataService;
            _energyHistoryDataService = energyHistoryDataService;
            _quarterlyFactsService = quarterlyFactsService;
            _investmentDataService = investmentDataService;
            _investmentGroupDataService = investmentGroupDataService;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
        }

        public async Task<CycleHistoryResult> GetAsync()
        {
            // ── Measured ─────────────────────────────────────────────────────
            var measurements = await _measurementDataService.GetList(set =>
                Task.FromResult(set.Where(m => m.IsReliable).ToList()));

            // Duplicates exist (see QuarterlyFactsService.GetAsync): keep the newest row per quarter.
            var measured = QuarterlyFactsService.NewestPerQuarter(measurements)
                .ToDictionary(m => m.Time, m => m.BatteryDischargedKWh);

            var perBattery = (await _batteryMeasurementDataService.GetList(set => Task.FromResult(set.ToList())))
                .GroupBy(m => m.BatteryId)
                .ToDictionary(g => g.Key, g => g.GroupBy(m => m.Time).ToDictionary(t => t.Key, t => t.Last().DischargedKWh));

            // ── Reconstructed ────────────────────────────────────────────────
            var histories = await _energyHistoryDataService.GetList(set => Task.FromResult(set.ToList()));
            var grid = QuarterlyFactsService.GridDeltas(histories, maxInterval: TimeSpan.FromMinutes(15));
            var solar = await _quarterlyFactsService.GetSolarByQuarterAsync(DateTime.MinValue, DateTime.MaxValue);
            var consumption = await _quarterlyFactsService.GetConsumptionByQuarterAsync(DateTime.MinValue, DateTime.MaxValue);

            var reconstructed = Reconstruct(consumption, solar, grid);

            // ── Batteries and when they came into use ────────────────────────
            var groups = await _investmentGroupDataService.GetList(set => Task.FromResult(set.ToList()));
            var storageGroupIds = groups.Where(g => g.Category == InvestmentCategory.Storage).Select(g => g.Id).ToHashSet();

            var investments = (await _investmentDataService.GetList(set => Task.FromResult(set.ToList())))
                .Where(i => i.InvestmentGroupId.HasValue && storageGroupIds.Contains(i.InvestmentGroupId.Value) && i.CapacityWh > 0.0)
                .ToList();

            var firstData = new[] { measured.Keys, reconstructed.Keys }
                .SelectMany(k => k)
                .DefaultIfEmpty(_timeZoneService.Now)
                .Min();

            var batteries = Batteries(investments, firstData);

            return Build(measured, reconstructed, perBattery, batteries, _timeZoneService.Now.DateFloorQuarter());
        }

        /// <summary>Configured batteries, each active from its own investment's purchase date (matched by name).</summary>
        private List<CycleBattery> Batteries(List<Investment> investments, DateTime firstData)
        {
            var earliestPurchase = investments.Count > 0 ? investments.Min(i => i.PurchaseDate) : firstData;

            return (_batteryContainer.Batteries ?? new List<Battery>())
                .Select(b =>
                {
                    var name = b.GetName() ?? b.Id;
                    // By name, else by number ("Battery 1" ↔ "Sessy 1"), else the earliest purchase.
                    var investment = investments.FirstOrDefault(i => string.Equals(i.Description?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                                  ?? investments.FirstOrDefault(i => Number(i.Description) is string n && (n == Number(name) || n == Number(b.Id)));

                    return new CycleBattery(b.Id, name, b.GetCapacity() / 1000.0, (investment?.PurchaseDate ?? earliestPurchase).DateFloorQuarter());
                })
                .ToList();
        }

        /// <summary>The digits in a name ("Sessy 2" → "2"); null when there are none.</summary>
        public static string? Number(string? name)
        {
            var digits = new string((name ?? string.Empty).Where(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : null;
        }

        /// <summary>
        /// Discharged kWh per quarter as the remainder of the energy balance: battery = consumption − solar − grid.
        /// Only quarters with both a consumption row and a grid delta; consumption timestamps are floored to the quarter.
        /// </summary>
        public static Dictionary<DateTime, double> Reconstruct(
            IReadOnlyDictionary<DateTime, double> consumptionAverageW,
            IReadOnlyDictionary<DateTime, double> solarKWh,
            IReadOnlyDictionary<DateTime, (double importWh, double exportWh)> grid)
        {
            var consumptionByQuarter = consumptionAverageW
                .GroupBy(kv => kv.Key.DateFloorQuarter())
                .ToDictionary(g => g.Key, g => g.Average(kv => kv.Value));

            var result = new Dictionary<DateTime, double>();

            foreach (var (time, (importWh, exportWh)) in grid)
            {
                if (!consumptionByQuarter.TryGetValue(time, out var averageW))
                    continue;

                solarKWh.TryGetValue(time, out var solar);

                var batteryKWh = Units.QuarterKWh(averageW) - solar - (importWh - exportWh) / 1000.0;
                result[time] = Math.Max(0.0, batteryKWh);
            }

            return result;
        }

        public static CycleHistoryResult Build(
            IReadOnlyDictionary<DateTime, double> measuredKWh,
            IReadOnlyDictionary<DateTime, double> reconstructedKWh,
            IReadOnlyDictionary<string, Dictionary<DateTime, double>> perBatteryKWh,
            IReadOnlyList<CycleBattery> batteries,
            DateTime now)
        {
            var empty = new CycleHistoryResult(new CycleSeries("Total", 0.0, null, new()), new());

            if (batteries.Count == 0)
                return empty;

            var start = batteries.Min(b => b.ActiveFrom);
            var totalCapacity = batteries.Sum(b => b.CapacityKWh);

            double ActiveCapacity(DateTime t) => batteries.Where(b => b.ActiveFrom <= t).Sum(b => b.CapacityKWh);

            // Estimate: the average measured cycles per quarter, idle quarters included.
            var measuredCycles = measuredKWh
                .Where(kv => kv.Key >= start && ActiveCapacity(kv.Key) > 0.0)
                .Select(kv => kv.Value / ActiveCapacity(kv.Key))
                .ToList();

            var cyclesPerQuarter = measuredCycles.Count > 0 ? measuredCycles.Average() : 0.0;

            var bank = new Accumulator();
            var own = batteries.ToDictionary(b => b.Id, _ => new Accumulator());

            for (var t = start; t <= now; t = t.AddMinutes(15))
            {
                var capacity = ActiveCapacity(t);
                if (capacity <= 0.0) continue;

                // Bank: best source per quarter.
                double bankCycles;
                if (measuredKWh.TryGetValue(t, out var m))
                    bank.Add(t, measured: bankCycles = m / capacity);
                else if (reconstructedKWh.TryGetValue(t, out var r))
                    bank.Add(t, reconstructed: bankCycles = r / capacity);
                else
                    bank.Add(t, estimated: bankCycles = cyclesPerQuarter);

                // Per battery: its own reading, else the bank's cycles for this quarter.
                foreach (var battery in batteries.Where(b => b.ActiveFrom <= t))
                {
                    if (perBatteryKWh.TryGetValue(battery.Id, out var readings) && readings.TryGetValue(t, out var kWh))
                        own[battery.Id].Add(t, measured: kWh / battery.CapacityKWh);
                    else
                        own[battery.Id].Add(t, estimated: bankCycles);
                }
            }

            return new CycleHistoryResult(
                new CycleSeries("Total", totalCapacity, start, bank.Months()),
                batteries.Select(b => new CycleSeries(b.Name, b.CapacityKWh, b.ActiveFrom, own[b.Id].Months())).ToList());
        }

        /// <summary>Sums cycles per month and source.</summary>
        private sealed class Accumulator
        {
            private readonly SortedDictionary<DateTime, double[]> _months = new();

            public void Add(DateTime t, double measured = 0.0, double reconstructed = 0.0, double estimated = 0.0)
            {
                var key = t.MonthStart();
                if (!_months.TryGetValue(key, out var sums))
                    _months[key] = sums = new double[3];

                sums[0] += measured;
                sums[1] += reconstructed;
                sums[2] += estimated;
            }

            public List<CycleMonth> Months()
            {
                var result = new List<CycleMonth>();
                double withEstimate = 0.0, withoutEstimate = 0.0;

                foreach (var (month, s) in _months)
                {
                    withEstimate += s[0] + s[1] + s[2];
                    withoutEstimate += s[0] + s[1];
                    result.Add(new CycleMonth(month, s[0], s[1], s[2], withEstimate, withoutEstimate));
                }

                return result;
            }
        }
    }
}
