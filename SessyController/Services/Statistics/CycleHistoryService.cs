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

    /// <summary>What decides the expected end of life.</summary>
    public enum LifeLimit { None, Cycles, Age, Capacity }

    /// <summary>
    /// Expected end of life: by cycles (rated cycles left at the current pace), by age (purchase + rated lifetime in
    /// years) and by capacity (measured fade reaching CapacityHealth.EndOfLifePercent), whichever comes first.
    /// </summary>
    public sealed record CycleLifetime(string Name, double UsedCycles, int RatedCycles, double CyclesPerYear, bool PaceOverFullYear,
                                       DateTime? CyclesEnd, DateTime? CalendarEnd, CapacityHealth? Health = null,
                                       DateTime? Start = null)
    {
        public DateTime? CapacityEnd => Health?.EndOfLife;

        /// <summary>The earliest of the ends; null when none is known.</summary>
        public DateTime? End => new[] { CyclesEnd, CalendarEnd, CapacityEnd }.Where(d => d.HasValue).Min();

        public LifeLimit LimitedBy =>
            End == null ? LifeLimit.None
            : End == CapacityEnd ? LifeLimit.Capacity
            : End == CyclesEnd ? LifeLimit.Cycles
            : LifeLimit.Age;

        public double? UsedPercent => RatedCycles > 0 ? UsedCycles / RatedCycles * 100.0 : null;
    }

    /// <summary>The cycle history of the whole bank or of one battery.</summary>
    public sealed record CycleSeries(string Name, double CapacityKWh, DateTime? Start, List<CycleMonth> Months,
                                     int RatedCycles = 0, int LifetimeYears = 0, CapacityHealth? Health = null)
    {
        /// <summary>Beyond this a forecast says nothing useful.</summary>
        private const double MaxForecastYears = 100.0;

        /// <summary>Less history than this gives no pace.</summary>
        private const double MinPaceDays = 30.0;

        public double TotalCycles(bool withEstimate) =>
            Months.Sum(m => m.KnownCycles + (withEstimate ? m.EstimatedCycles : 0.0));

        private double CyclesOf(CycleMonth month, bool withEstimate) =>
            month.KnownCycles + (withEstimate ? month.EstimatedCycles : 0.0);

        /// <summary>
        /// Forecast from the pace of the last twelve complete months (a full year evens out the seasons);
        /// with less history the average since the start, which leans towards the season it covers.
        /// </summary>
        public CycleLifetime Lifetime(bool withEstimate, DateTime now)
        {
            var used = TotalCycles(withEstimate);
            var thisMonth = now.MonthStart();
            var yearAgo = thisMonth.AddMonths(-12);

            double pace = 0.0;
            bool fullYear = Start.HasValue && Start.Value <= yearAgo;

            if (fullYear)
                pace = Months.Where(m => m.Month >= yearAgo && m.Month < thisMonth).Sum(m => CyclesOf(m, withEstimate));
            else if (Start.HasValue && (now - Start.Value).TotalDays >= MinPaceDays)
                pace = used / ((now - Start.Value).TotalDays / 365.25);

            DateTime? cyclesEnd = null;
            if (RatedCycles > 0 && pace > 0.0)
            {
                var years = Math.Max(0.0, RatedCycles - used) / pace;
                if (years <= MaxForecastYears)
                    cyclesEnd = now.Date.AddDays(years * 365.25);
            }

            DateTime? calendarEnd = LifetimeYears > 0 && Start.HasValue ? Start.Value.Date.AddYears(LifetimeYears) : null;

            return new CycleLifetime(Name, used, RatedCycles, pace, fullYear, cyclesEnd, calendarEnd, Health, Start);
        }
    }

    public sealed record CycleHistoryResult(CycleSeries Total, List<CycleSeries> Batteries)
    {
        /// <summary>Average lifetime cycles over the batteries.</summary>
        public double AveragePerBattery(bool withEstimate) =>
            Batteries.Count > 0 ? Batteries.Average(b => b.TotalCycles(withEstimate)) : Total.TotalCycles(withEstimate);

        /// <summary>Lifetime forecast of every battery, or of the bank when there are none.</summary>
        public List<CycleLifetime> Lifetimes(bool withEstimate, DateTime now) =>
            (Batteries.Count > 0 ? Batteries : new List<CycleSeries> { Total }).Select(b => b.Lifetime(withEstimate, now)).ToList();

        /// <summary>The battery matching a name ("Sessy 2" also matches "Battery 2"); null when none does.</summary>
        public CycleSeries? Find(string? name) =>
            Batteries.FirstOrDefault(b => string.Equals(b.Name.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? Batteries.FirstOrDefault(b => CycleHistoryService.Number(name) is string n && n == CycleHistoryService.Number(b.Name));
    }

    /// <summary>One battery as the history sees it: capacity and the first quarter it was in use.</summary>
    public sealed record CycleBattery(string Id, string Name, double CapacityKWh, DateTime ActiveFrom,
                                      int RatedCycles = 0, int LifetimeYears = 0);

    /// <summary>
    /// Full cycles since the batteries were bought. Per quarter the discharged energy is taken from the best
    /// source there is: measured (QuarterlyMeasurements), reconstructed (consumption − solar − grid, where the
    /// battery is the remainder of the energy balance) or estimated (the measured average cycles per quarter).
    /// Per battery only its own readings count as measured; before those its cycles follow the bank's, as
    /// reconstructed where the bank has data and as estimated where the bank's are an estimate too.
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

        private readonly object _lock = new();
        private Task<CycleHistoryResult>? _load;

        /// <summary>
        /// The cycle history, loaded once per scope (one browser session) and shared by every caller:
        /// it reads all battery quarters since the start of recording, so loading it twice at the same
        /// time doubles the database load. Pass refresh to load it again.
        /// </summary>
        public Task<CycleHistoryResult> GetAsync(bool refresh = false)
        {
            lock (_lock)
            {
                if (refresh || _load == null || _load.IsFaulted || _load.IsCanceled)
                    _load = LoadAsync();

                return _load;
            }
        }

        private async Task<CycleHistoryResult> LoadAsync()
        {
            // Every read is independent: run them side by side, each on its own context and thread
            // (the SQLite provider works synchronously, so without Task.Run they would queue up).
            // Only the columns the cycles need are read.
            var measurementsTask = Task.Run(() => _measurementDataService.GetList(set => Task.FromResult(set
                .Where(m => m.IsReliable)
                .Select(m => new QuarterlyMeasurement { Id = m.Id, Time = m.Time, BatteryPowerWatts = m.BatteryPowerWatts })
                .ToList())));
            var batteryRowsTask = Task.Run(() => _batteryMeasurementDataService.GetList(set => Task.FromResult(set
                .Select(m => new BatteryQuarterMeasurement
                {
                    Id = m.Id, BatteryId = m.BatteryId, Time = m.Time, PowerWatts = m.PowerWatts,
                    StateOfChargeWh = m.StateOfChargeWh, CapacityWh = m.CapacityWh
                })
                .ToList())));
            var historiesTask = Task.Run(() => _energyHistoryDataService.GetList(set => Task.FromResult(set.ToList())));
            var solarTask = Task.Run(() => _quarterlyFactsService.GetSolarByQuarterAsync(DateTime.MinValue, DateTime.MaxValue));
            var consumptionTask = Task.Run(() => _quarterlyFactsService.GetConsumptionByQuarterAsync(DateTime.MinValue, DateTime.MaxValue));
            var groupsTask = Task.Run(() => _investmentGroupDataService.GetList(set => Task.FromResult(set.ToList())));
            var investmentsTask = Task.Run(() => _investmentDataService.GetList(set => Task.FromResult(set.ToList())));

            await Task.WhenAll(measurementsTask, batteryRowsTask, historiesTask, solarTask, consumptionTask, groupsTask, investmentsTask);

            // ── Measured ─────────────────────────────────────────────────────
            // Duplicates exist (see QuarterlyFactsService.GetAsync): keep the newest row per quarter.
            var measured = QuarterlyFactsService.NewestPerQuarter(measurementsTask.Result)
                .ToDictionary(m => m.Time, m => m.BatteryDischargedKWh);

            var rowsPerBattery = batteryRowsTask.Result.GroupBy(m => m.BatteryId).ToDictionary(g => g.Key, g => g.ToList());

            var perBattery = rowsPerBattery
                .ToDictionary(kv => kv.Key, kv => kv.Value.GroupBy(m => m.Time).ToDictionary(t => t.Key, t => t.Last().DischargedKWh));

            // Measured capacity per battery, from its long discharges.
            var now = _timeZoneService.Now;
            var health = rowsPerBattery
                .Select(kv => (kv.Key, Health: BatteryHealth.Estimate(kv.Value, now)))
                .Where(h => h.Health != null)
                .ToDictionary(h => h.Key, h => h.Health!);

            // ── Reconstructed ────────────────────────────────────────────────
            var grid = QuarterlyFactsService.GridDeltas(historiesTask.Result, maxInterval: TimeSpan.FromMinutes(15));
            var reconstructed = Reconstruct(consumptionTask.Result, solarTask.Result, grid);

            // ── Batteries and when they came into use ────────────────────────
            var storageGroupIds = groupsTask.Result.Where(g => g.Category == InvestmentCategory.Storage).Select(g => g.Id).ToHashSet();

            var investments = investmentsTask.Result
                .Where(i => i.InvestmentGroupId.HasValue && storageGroupIds.Contains(i.InvestmentGroupId.Value) && i.CapacityWh > 0.0)
                .ToList();

            var firstData = new[] { measured.Keys, reconstructed.Keys }
                .SelectMany(k => k)
                .DefaultIfEmpty(_timeZoneService.Now)
                .Min();

            var batteries = Batteries(investments, firstData);

            return Build(measured, reconstructed, perBattery, batteries, now.DateFloorQuarter(), health);
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

                    return new CycleBattery(b.Id, name, b.GetCapacity() / 1000.0, (investment?.PurchaseDate ?? earliestPurchase).DateFloorQuarter(),
                                            investment?.ExpectedTotalCycles ?? 0, investment?.ExpectedLifetimeYears ?? 0);
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
            DateTime now,
            IReadOnlyDictionary<string, CapacityHealth>? health = null)
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
                bool bankEstimated = false;
                if (measuredKWh.TryGetValue(t, out var m))
                    bank.Add(t, measured: bankCycles = m / capacity);
                else if (reconstructedKWh.TryGetValue(t, out var r))
                    bank.Add(t, reconstructed: bankCycles = r / capacity);
                else
                {
                    bank.Add(t, estimated: bankCycles = cyclesPerQuarter);
                    bankEstimated = true;
                }

                // Per battery: its own reading; else the bank's cycles, derived from data unless the bank's are an estimate.
                foreach (var battery in batteries.Where(b => b.ActiveFrom <= t))
                {
                    if (perBatteryKWh.TryGetValue(battery.Id, out var readings) && readings.TryGetValue(t, out var kWh))
                        own[battery.Id].Add(t, measured: kWh / battery.CapacityKWh);
                    else if (bankEstimated)
                        own[battery.Id].Add(t, estimated: bankCycles);
                    else
                        own[battery.Id].Add(t, reconstructed: bankCycles);
                }
            }

            // Bank: capacity-weighted rated cycles; it lasts as long as its first battery to reach its rated age.
            var rated = batteries.Where(b => b.RatedCycles > 0).ToList();
            var ratedCapacity = rated.Sum(b => b.CapacityKWh);
            var bankRatedCycles = ratedCapacity > 0.0 ? (int)Math.Round(rated.Sum(b => b.CapacityKWh * b.RatedCycles) / ratedCapacity) : 0;
            var aged = batteries.Where(b => b.LifetimeYears > 0).ToList();
            var bankLifetimeYears = aged.Count > 0 ? aged.Min(b => b.LifetimeYears) : 0;

            return new CycleHistoryResult(
                new CycleSeries("Total", totalCapacity, start, bank.Months(), bankRatedCycles, bankLifetimeYears),
                batteries.Select(b => new CycleSeries(b.Name, b.CapacityKWh, b.ActiveFrom, own[b.Id].Months(), b.RatedCycles, b.LifetimeYears,
                                                      health != null && health.TryGetValue(b.Id, out var h) ? h : null)).ToList());
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
