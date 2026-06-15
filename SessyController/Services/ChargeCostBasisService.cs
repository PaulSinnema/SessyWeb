using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Tracks the cost basis (purchase price) of the energy currently stored in the
    /// batteries using a FIFO layer model.
    ///
    /// Each charge event pushes a layer { Wh, costEurPerKWh } onto a queue:
    ///   - The solar portion of the charge has cost 0 (free).
    ///   - The grid portion has cost = real buying price (incl. tax/VAT/compensation)
    ///     computed via the CalculationService.
    ///
    /// Each discharge event pops the oldest Wh first (FIFO).
    ///
    /// The service is restartable: on first use it rebuilds the FIFO queue by replaying
    /// QuarterlyMeasurements + InverterMeasurements history from the last moment the
    /// battery was (near) empty up to now. No separate persisted state is required —
    /// the queue is always consistent with the actual measurements.
    /// </summary>
    public class ChargeCostBasisService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ICalculationService _calculationService;
        private readonly TimeZoneService _timeZoneService;
        private readonly LoggingService<ChargeCostBasisService> _logger;

        // FIFO queue of charge layers. Front = oldest energy (next to be discharged).
        private readonly LinkedList<ChargeLayer> _layers = new();

        // Time of the last measurement already folded into the queue.
        private DateTime _lastProcessedTime = DateTime.MinValue;

        // SOC below which the battery is considered "empty" for replay anchoring (Wh).
        private const double EmptyThresholdWh = 100.0;

        // How far back to look for an empty anchor before giving up and using a window.
        private const int MaxReplayDays = 14;

        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public ChargeCostBasisService(
            IServiceScopeFactory serviceScopeFactory,
            ICalculationService calculationService,
            TimeZoneService timeZoneService,
            LoggingService<ChargeCostBasisService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _calculationService = calculationService;
            _timeZoneService = timeZoneService;
            _logger = logger;
        }

        /// <summary>
        /// A single FIFO layer of stored energy with its acquisition cost.
        /// </summary>
        private sealed class ChargeLayer
        {
            public double Wh;               // remaining energy in this layer
            public double CostEurPerKWh;    // acquisition cost (0 for solar)
        }

        /// <summary>
        /// Immutable snapshot of the current cost-basis state for display in the UI.
        /// </summary>
        public sealed record CostBasisSnapshot(
            double TrackedWh,
            double OldestLayerPriceEur,
            double AverageCostBasisEur,
            IReadOnlyList<CostBasisLayerInfo> Layers);

        /// <summary>One FIFO layer for display (oldest first). Index gives a unique key.</summary>
        public sealed record CostBasisLayerInfo(int Index, double Wh, double CostEurPerKWh, bool IsSolar);

        /// <summary>
        /// Returns a snapshot of the current FIFO queue for display: total tracked energy,
        /// oldest-layer price, weighted-average cost basis, and the individual layers.
        /// </summary>
        public async Task<CostBasisSnapshot> GetSnapshotAsync()
        {
            await EnsureUpToDateAsync().ConfigureAwait(false);

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                double totalWh = 0.0, totalCost = 0.0, oldest = 0.0;
                bool oldestSet = false;
                var layers = new List<CostBasisLayerInfo>(_layers.Count);

                int idx = 0;
                foreach (var l in _layers)
                {
                    totalWh += l.Wh;
                    totalCost += l.Wh / 1000.0 * l.CostEurPerKWh;
                    if (!oldestSet && l.Wh > 1.0)
                    {
                        oldest = l.CostEurPerKWh;
                        oldestSet = true;
                    }
                    layers.Add(new CostBasisLayerInfo(idx++, l.Wh, l.CostEurPerKWh, l.CostEurPerKWh < 0.0001));
                }

                double avg = totalWh > 1.0 ? totalCost / (totalWh / 1000.0) : 0.0;
                return new CostBasisSnapshot(totalWh, oldest, avg, layers);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Cost (EUR/kWh) of the oldest stored energy — the energy that would be
        /// discharged next under FIFO. Used for the runtime "discharge now?" decision:
        /// discharging is profitable when sellPrice &gt; this + cycleCost.
        /// Returns 0 when the battery is effectively empty.
        /// </summary>
        public async Task<double> GetOldestLayerPriceEur()
        {
            await EnsureUpToDateAsync().ConfigureAwait(false);

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var node = _layers.First;
                while (node != null)
                {
                    if (node.Value.Wh > 1.0)
                        return node.Value.CostEurPerKWh;
                    node = node.Next;
                }
                return 0.0;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Weighted-average cost basis (EUR/kWh) of all energy currently stored.
        /// Used to seed the solver's begin-SOC so it does not treat stored charge as free.
        /// Returns 0 when the battery is effectively empty.
        /// </summary>
        public async Task<double> GetAverageCostBasisEur()
        {
            await EnsureUpToDateAsync().ConfigureAwait(false);

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                double totalWh = 0.0, totalCost = 0.0;
                foreach (var layer in _layers)
                {
                    totalWh += layer.Wh;
                    totalCost += layer.Wh / 1000.0 * layer.CostEurPerKWh;
                }
                return totalWh > 1.0 ? totalCost / (totalWh / 1000.0) : 0.0;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Total energy currently tracked in the FIFO queue (Wh). For diagnostics.
        /// </summary>
        public async Task<double> GetTrackedWh()
        {
            await EnsureUpToDateAsync().ConfigureAwait(false);

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return _layers.Sum(l => l.Wh);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Ensures the FIFO queue reflects all measurements up to now.
        /// On first call it rebuilds from history; subsequent calls fold in only
        /// the measurements that arrived since the last processed time.
        /// </summary>
        private async Task EnsureUpToDateAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                bool firstRun = _lastProcessedTime == DateTime.MinValue;
                if (firstRun)
                    await RebuildFromHistoryAsync().ConfigureAwait(false);
                else
                    await AppendNewMeasurementsAsync(_lastProcessedTime).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"ChargeCostBasisService update failed: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Rebuilds the entire FIFO queue by replaying measurements from the most
        /// recent "empty" anchor up to now. Caller must hold the semaphore.
        /// </summary>
        private async Task RebuildFromHistoryAsync()
        {
            _layers.Clear();

            var now = _timeZoneService.Now;
            var earliest = now.AddDays(-MaxReplayDays);

            var measurements = await LoadMeasurementsAsync(earliest, now).ConfigureAwait(false);
            if (measurements.Count == 0)
            {
                _lastProcessedTime = now;
                return;
            }

            // Find the last quarter where the battery was (near) empty; replay from there.
            int startIndex = 0;
            for (int i = measurements.Count - 1; i >= 0; i--)
            {
                if (measurements[i].BatteryStateOfChargeWh <= EmptyThresholdWh)
                {
                    startIndex = i;
                    break;
                }
            }

            var replaySlice = measurements.GetRange(startIndex, measurements.Count - startIndex);
            var solarByTime = await LoadSolarAsync(
                replaySlice.First().Time, replaySlice.Last().Time).ConfigureAwait(false);
            var prices = await _calculationService
                .CalculateEnergyPricesBatchAsync(replaySlice.Select(m => m.Time))
                .ConfigureAwait(false);

            foreach (var m in replaySlice)
                ApplyMeasurement(m, solarByTime, prices);

            // BatteryPowerWatts is AC-side, so charge/discharge accounting drifts from
            // the DC-side measured SOC over many cycles. Resync the queue total to the
            // last measured SOC: trim oldest layers if over (already consumed), or pad
            // with a free layer if under (unknown origin, treated as free).
            ResyncToMeasuredSoc(replaySlice.Last().BatteryStateOfChargeWh);

            _lastProcessedTime = replaySlice.Last().Time;

            _logger.LogInformation(
                $"ChargeCostBasis rebuilt: {_layers.Count} layers, " +
                $"{_layers.Sum(l => l.Wh):F0} Wh tracked from {replaySlice.First().Time:yyyy-MM-dd HH:mm}.");
        }

        /// <summary>
        /// Folds measurements newer than <paramref name="since"/> into the queue.
        /// Caller must hold the semaphore.
        /// </summary>
        private async Task AppendNewMeasurementsAsync(DateTime since)
        {
            var now = _timeZoneService.Now;
            var measurements = await LoadMeasurementsAsync(since.AddMinutes(1), now).ConfigureAwait(false);
            if (measurements.Count == 0)
                return;

            var solarByTime = await LoadSolarAsync(
                measurements.First().Time, measurements.Last().Time).ConfigureAwait(false);
            var prices = await _calculationService
                .CalculateEnergyPricesBatchAsync(measurements.Select(m => m.Time))
                .ConfigureAwait(false);

            foreach (var m in measurements)
                ApplyMeasurement(m, solarByTime, prices);

            ResyncToMeasuredSoc(measurements.Last().BatteryStateOfChargeWh);

            _lastProcessedTime = measurements.Last().Time;
        }

        /// <summary>
        /// Applies a single quarter measurement to the FIFO queue:
        /// charging pushes a layer (solar portion free, grid portion at buy price),
        /// discharging pops the oldest energy first.
        /// </summary>
        private void ApplyMeasurement(
            QuarterlyMeasurement m,
            IReadOnlyDictionary<DateTime, double> solarByTime,
            IReadOnlyDictionary<DateTime, EnergyPrice> prices)
        {
            double chargedWh = m.BatteryChargedKWh * 1000.0;
            double dischargedWh = m.BatteryDischargedKWh * 1000.0;

            if (chargedWh > 1.0)
            {
                double buyPrice = prices.TryGetValue(m.Time, out var p) ? p.Buying : 0.0;

                // Solar produced this quarter (Wh). The portion of the charge covered by
                // solar is free; the remainder is grid energy at the buying price.
                double solarWh = solarByTime.TryGetValue(m.Time, out var s) ? s * 1000.0 : 0.0;

                double solarChargeWh = Math.Min(chargedWh, Math.Max(solarWh, 0.0));
                double gridChargeWh = Math.Max(chargedWh - solarChargeWh, 0.0);

                if (solarChargeWh > 1.0)
                    _layers.AddLast(new ChargeLayer { Wh = solarChargeWh, CostEurPerKWh = 0.0 });
                if (gridChargeWh > 1.0)
                    _layers.AddLast(new ChargeLayer { Wh = gridChargeWh, CostEurPerKWh = buyPrice });
            }
            else if (dischargedWh > 1.0)
            {
                RemoveOldest(dischargedWh);
            }
        }

        /// <summary>
        /// Reconciles the FIFO queue total with the measured DC-side SOC.
        /// Over-tracking (AC accounting drift) trims the oldest layers — those were
        /// effectively already consumed. Under-tracking pads a free layer at the back.
        /// </summary>
        private void ResyncToMeasuredSoc(double measuredSocWh)
        {
            if (measuredSocWh < 0.0) measuredSocWh = 0.0;

            double tracked = _layers.Sum(l => l.Wh);
            double diff = tracked - measuredSocWh;

            if (diff > 1.0)
            {
                // Over-tracked: remove the oldest energy first (FIFO).
                RemoveOldest(diff);
            }
            else if (diff < -1.0)
            {
                // Under-tracked: pad with a free layer of unknown origin.
                _layers.AddLast(new ChargeLayer { Wh = -diff, CostEurPerKWh = 0.0 });
            }
        }

        /// <summary>Removes the requested Wh from the front (oldest) of the FIFO queue.</summary>
        private void RemoveOldest(double wh)
        {
            double remaining = wh;
            while (remaining > 1.0 && _layers.First != null)
            {
                var layer = _layers.First.Value;
                if (layer.Wh <= remaining)
                {
                    remaining -= layer.Wh;
                    _layers.RemoveFirst();
                }
                else
                {
                    layer.Wh -= remaining;
                    remaining = 0.0;
                }
            }
        }

        /// <summary>Loads reliable quarter measurements in [from, to], ordered by time.</summary>
        private async Task<List<QuarterlyMeasurement>> LoadMeasurementsAsync(DateTime from, DateTime to)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dataService = scope.ServiceProvider.GetRequiredService<QuarterlyMeasurementDataService>();

            return await dataService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= from && m.Time <= to)
                    .OrderBy(m => m.Time)
                    .ToList();
                return await Task.FromResult(result);
            }).ConfigureAwait(false);
        }

        /// <summary>Loads solar production (kWh) per quarter in [from, to].</summary>
        private async Task<Dictionary<DateTime, double>> LoadSolarAsync(DateTime from, DateTime to)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dataService = scope.ServiceProvider.GetRequiredService<InverterMeasurementDataService>();

            var list = await dataService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= from && m.Time <= to)
                    .ToList();
                return await Task.FromResult(result);
            }).ConfigureAwait(false);

            // Multiple inverters can report for the same quarter — sum them.
            return list
                .GroupBy(m => m.Time)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.SolarProductionKWh));
        }
    }
}