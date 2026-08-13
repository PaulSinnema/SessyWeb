using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// The one place a measured quarter is assembled. Every reader of measured energy goes through
    /// here, so each physical quantity has exactly one authoritative table:
    ///
    ///   grid import/export  ← EnergyHistory   (delta between consecutive cumulative meter readings)
    ///   solar production    ← InverterMeasurements
    ///   household load      ← Consumption     (measured solar + grid + battery)
    ///   battery power/SOC   ← QuarterlyMeasurements
    ///   prices              ← EPEXPrices + Taxes, via CalculationService
    ///
    /// Before this existed the grid delta was implemented four separate times (the statistics
    /// service, ChargeCostBasisService, GridPower and a month-boundary variant), two of which
    /// clamped negative deltas and one of which did not, and household consumption existed twice:
    /// measured in the Consumption table and re-derived as an energy balance that left the battery
    /// out altogether, so charging from the grid counted as household load.
    /// </summary>
    public class QuarterlyFactsService
    {
        private readonly QuarterlyMeasurementDataService _measurementDataService;
        private readonly EnergyHistoryDataService _energyHistoryDataService;
        private readonly InverterMeasurementDataService _inverterMeasurementDataService;
        private readonly ConsumptionDataService _consumptionDataService;
        private readonly ICalculationService _calculationService;

        public QuarterlyFactsService(
            QuarterlyMeasurementDataService measurementDataService,
            EnergyHistoryDataService energyHistoryDataService,
            InverterMeasurementDataService inverterMeasurementDataService,
            ConsumptionDataService consumptionDataService,
            ICalculationService calculationService)
        {
            _measurementDataService = measurementDataService;
            _energyHistoryDataService = energyHistoryDataService;
            _inverterMeasurementDataService = inverterMeasurementDataService;
            _consumptionDataService = consumptionDataService;
            _calculationService = calculationService;
        }

        // ── Pure helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Grid import and export in Wh over the interval between two consecutive meter readings.
        ///
        /// Both are clamped at zero: the registers only ever count up, so a negative delta means a
        /// meter reset or a gap in the history, never energy flowing the other way. GridPower used
        /// to skip that clamp, which turned a reset into revenue on the financial page.
        /// </summary>
        public static (double importWh, double exportWh) GridDelta(EnergyHistory previous, EnergyHistory current)
        {
            double importWh = (current.ConsumedTariff1 - previous.ConsumedTariff1)
                            + (current.ConsumedTariff2 - previous.ConsumedTariff2);

            double exportWh = (current.ProducedTariff1 - previous.ProducedTariff1)
                            + (current.ProducedTariff2 - previous.ProducedTariff2);

            return (Math.Max(0.0, importWh), Math.Max(0.0, exportWh));
        }

        /// <summary>
        /// Grid deltas for a whole ordered run of meter readings, keyed by the END time of each
        /// interval — that is the quarter the energy belongs to. Readings for several meters are
        /// handled per meter and then summed, so a second P1 meter no longer differences against
        /// the other one's registers.
        /// </summary>
        public static Dictionary<DateTime, (double importWh, double exportWh)> GridDeltas(
            IEnumerable<EnergyHistory> histories)
        {
            var result = new Dictionary<DateTime, (double importWh, double exportWh)>();

            foreach (var perMeter in histories.GroupBy(h => h.MeterId))
            {
                var ordered = perMeter.OrderBy(h => h.Time).ToList();

                for (int i = 1; i < ordered.Count; i++)
                {
                    var (importWh, exportWh) = GridDelta(ordered[i - 1], ordered[i]);
                    var time = ordered[i].Time;

                    result.TryGetValue(time, out var running);
                    result[time] = (running.importWh + importWh, running.exportWh + exportWh);
                }
            }

            return result;
        }

        // ── Assembly ─────────────────────────────────────────────────────────

        /// <summary>
        /// Every measured quarter in [start, end], one MeasurementView each.
        ///
        /// The spine is QuarterlyMeasurements, because that is the table with a row per quarter the
        /// system was actually running. A quarter without such a row carries no battery state, and
        /// including it would silently report a household that ran on nothing.
        /// </summary>
        public async Task<List<MeasurementView>> GetAsync(DateTime start, DateTime end)
        {
            var rows = await _measurementDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= start && m.Time <= end)
                    .ToList()));

            // One row per quarter, whatever the table holds. QuarterlyMeasurements is written by
            // two services (EnergyMonitorService creates it, BatteriesService updates it) and the
            // unique index on Time that migration RepairQuarterlyMeasurements created in raw SQL
            // is gone — a later EF table rebuild took it with it. The production database has 268
            // duplicate quarters as a result, and every one of them counted its battery energy and
            // its consumption twice. Keep the highest Id: that is the most recently written row.
            var measurements = rows
                .GroupBy(m => m.Time)
                .Select(g => g.OrderByDescending(m => m.Id).First())
                .OrderBy(m => m.Time)
                .ToList();

            // One quarter of run-up, so the first quarter inside the window has a predecessor to
            // difference against. DateTime.MinValue ("all data") has nothing before it and
            // subtracting from it throws.
            var historyStart = start > DateTime.MinValue.AddMinutes(15)
                ? start.AddMinutes(-15)
                : start;

            var histories = await _energyHistoryDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(h => h.Time >= historyStart && h.Time <= end)
                    .ToList()));

            var gridByTime = GridDeltas(histories);

            var solarByTime = await GetSolarByQuarterAsync(start, end);
            var consumptionByTime = await GetConsumptionByQuarterAsync(start, end);

            var prices = await _calculationService.CalculateEnergyPricesBatchAsync(
                measurements.Select(m => m.Time));

            return measurements.Select(m =>
            {
                gridByTime.TryGetValue(m.Time, out var grid);
                prices.TryGetValue(m.Time, out var price);

                bool hasConsumption = consumptionByTime.TryGetValue(m.Time, out double consumptionW);

                return new MeasurementView
                {
                    Time = m.Time,
                    BatteryPowerWatts = m.BatteryPowerWatts,
                    BatteryStateOfChargeWh = m.BatteryStateOfChargeWh,
                    BatteryMode = m.BatteryMode,
                    IsReliable = m.IsReliable,
                    PlannedRevenueEur = m.PlannedRevenueEur,
                    GridImportWh = grid.importWh,
                    GridExportWh = grid.exportWh,
                    SolarProductionKWh = solarByTime.TryGetValue(m.Time, out double kwh) ? kwh : 0.0,
                    ConsumptionAverageW = hasConsumption ? consumptionW : 0.0,
                    HasConsumption = hasConsumption,
                    BuyingPriceEur = price?.Buying ?? 0.0,
                    SellingPriceEur = price?.Selling ?? 0.0
                };
            }).ToList();
        }

        /// <summary>
        /// Solar production per quarter in kWh, summed over every inverter and provider — there is
        /// one InverterMeasurements row per inverter per quarter.
        /// </summary>
        public async Task<Dictionary<DateTime, double>> GetSolarByQuarterAsync(DateTime start, DateTime end)
        {
            var inverterMeasurements = await _inverterMeasurementDataService.GetList(async set =>
                await Task.FromResult(set.Where(m => m.Time >= start && m.Time <= end).ToList()));

            return inverterMeasurements
                .GroupBy(m => m.Time)
                .ToDictionary(g => g.Key, g => g.Sum(m => m.SolarProductionKWh));
        }

        /// <summary>
        /// Measured household load per quarter, in Watts averaged over that quarter — the unit
        /// Consumption.ConsumptionWh actually holds, whatever its name says. Rows that failed to
        /// measure are not stored at all, so a missing key means "unknown", not "zero".
        /// </summary>
        public async Task<Dictionary<DateTime, double>> GetConsumptionByQuarterAsync(DateTime start, DateTime end)
        {
            var consumption = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set.Where(c => c.Time >= start && c.Time <= end).ToList()));

            return consumption
                .GroupBy(c => c.Time)
                .ToDictionary(g => g.Key, g => g.Average(c => c.ConsumptionWh));
        }

        /// <summary>Total solar production in kWh over the period, straight from InverterMeasurements.</summary>
        public async Task<double> GetSolarProductionKWhAsync(DateTime start, DateTime end)
        {
            var solarByTime = await GetSolarByQuarterAsync(start, end);
            return solarByTime.Values.Sum();
        }
    }
}
