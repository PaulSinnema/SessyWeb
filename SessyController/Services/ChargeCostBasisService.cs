using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Tracks the cost basis (purchase price) of the energy stored in the batteries using a
    /// FIFO layer model, both backwards (measured history) and forwards (the current plan).
    ///
    /// Each charge event pushes layers { Wh, costEurPerKWh } onto a queue:
    ///   - The part covered by solar surplus never crosses the meter and costs 0.
    ///   - The part drawn from the grid carries the real buying price.
    /// Each discharge event pops the oldest Wh first (FIFO).
    ///
    /// Unit conventions — both matter and were wrong before v1.0.37:
    ///   - Layer Wh is *stored* (DC) energy; layer cost is EUR per *stored* kWh, so the charge
    ///     efficiency is already in it. Cost per *delivered* kWh is that value / dischargeEff.
    ///   - The grid/free split comes from what actually passed the meter — min(gridImport,
    ///     charged) — not from total solar production. Solar the house ate itself is not free
    ///     energy for the battery. Same convention as MeasurementView.GridChargeCostEur.
    ///
    /// The service is restartable: on first use it rebuilds the FIFO queue by replaying
    /// QuarterlyMeasurements + EnergyHistory meter deltas from the last moment the battery was
    /// (near) empty up to now. No separate persisted state is required — the queue is always
    /// consistent with the actual measurements.
    /// </summary>
    public class ChargeCostBasisService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ICalculationService _calculationService;
        private readonly BatteryEfficiencyService _batteryEfficiencyService;
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

        // Energy below this is rounding noise, not a layer.
        private const double MinLayerWh = 1.0;

        // The efficiency fit walks a month of measurements; it does not move within a cycle.
        private static readonly TimeSpan EfficiencyCacheTime = TimeSpan.FromHours(6);

        private (double Charge, double Discharge) _efficiencies = (1.0, 1.0);
        private DateTime _efficienciesFetchedAt = DateTime.MinValue;

        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public ChargeCostBasisService(
            IServiceScopeFactory serviceScopeFactory,
            ICalculationService calculationService,
            BatteryEfficiencyService batteryEfficiencyService,
            TimeZoneService timeZoneService,
            LoggingService<ChargeCostBasisService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _calculationService = calculationService;
            _batteryEfficiencyService = batteryEfficiencyService;
            _timeZoneService = timeZoneService;
            _logger = logger;
        }

        /// <summary>
        /// A single FIFO layer of stored energy with its acquisition cost.
        /// </summary>
        private sealed class ChargeLayer
        {
            public double Wh;               // remaining stored (DC) energy in this layer
            public double CostEurPerKWh;    // acquisition cost per stored kWh (0 for solar)
        }

        /// <summary>
        /// Immutable snapshot of the current cost-basis state for display in the UI.
        /// </summary>
        public sealed record CostBasisSnapshot(
            double TrackedWh,
            double OldestLayerPriceEur,
            double AverageCostBasisEur,
            double AverageCostBasisDeliveredEur,
            IReadOnlyList<CostBasisLayerInfo> Layers);

        /// <summary>One FIFO layer for display (oldest first). Index gives a unique key.</summary>
        public sealed record CostBasisLayerInfo(
            int Index,
            double Wh,
            double CostEurPerKWh,
            double CostEurPerKWhDelivered,
            bool IsSolar);

        /// <summary>
        /// One quarter of the plan, reduced to what the FIFO projection needs.
        /// SocWh is the planned end-of-quarter SOC, SolarSurplusWh the AC-side surplus
        /// available for free charging, BuyEurPerKWh the all-in buying price.
        /// </summary>
        public sealed record ProjectionStep(
            DateTime Time,
            double SocWh,
            double SolarSurplusWh,
            double BuyEurPerKWh);

        /// <summary>Projected cost basis of the battery contents at one quarter.</summary>
        public sealed record ProjectedQuarterCost(
            DateTime Time,
            double TrackedWh,
            double AvgStoredEurPerKWh,
            double AvgDeliveredEurPerKWh);

        /// <summary>
        /// The cost basis rolled forward through the current plan, plus what the plan intends
        /// to buy from the grid to get there.
        /// </summary>
        public sealed record CostBasisProjection(
            DateTime BuiltAt,
            IReadOnlyList<ProjectedQuarterCost> Quarters,
            IReadOnlyList<CostBasisLayerInfo> EndOfHorizonLayers,
            double PlannedGridChargeKWh,
            double PlannedGridChargeCostEur,
            double PlannedAverageBuyEurPerKWh,
            double EndOfHorizonStoredEurPerKWh,
            double EndOfHorizonDeliveredEurPerKWh);

        /// <summary>Most recent projection, kept so the UI can show it without a re-solve.</summary>
        public CostBasisProjection? LastProjection { get; private set; }

        /// <summary>
        /// Returns a snapshot of the current FIFO queue for display: total tracked energy,
        /// oldest-layer price, weighted-average cost basis (stored and delivered), and the
        /// individual layers.
        /// </summary>
        public async Task<CostBasisSnapshot> GetSnapshotAsync()
        {
            await EnsureUpToDateAsync().ConfigureAwait(false);

            double disEff = _efficiencies.Discharge;

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var (totalWh, totalCost) = Totals(_layers);

                double oldest = 0.0;
                foreach (var l in _layers)
                {
                    if (l.Wh > MinLayerWh)
                    {
                        oldest = l.CostEurPerKWh;
                        break;
                    }
                }

                double avg = totalWh > MinLayerWh ? totalCost / (totalWh / 1000.0) : 0.0;
                return new CostBasisSnapshot(totalWh, oldest, avg, Delivered(avg, disEff), Describe(_layers, disEff));
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

        // ── Forward projection ───────────────────────────────────────────────

        /// <summary>
        /// Rolls the measured FIFO stack forward through the planned quarters and reports the
        /// cost basis per quarter, the layers left at the end of the horizon, and what the plan
        /// intends to spend on grid charging along the way.
        ///
        /// The SOC delta drives it rather than the planned power, because that also captures
        /// ZeroNetHome — a quarter that stores solar while covering the house at the same time.
        /// </summary>
        public async Task<CostBasisProjection> ProjectAsync(
            IReadOnlyList<ProjectionStep> steps,
            double currentSocWh)
        {
            var seed = await GetSnapshotAsync().ConfigureAwait(false);

            var projection = Project(
                seed.Layers, steps, currentSocWh,
                _efficiencies.Charge, _efficiencies.Discharge, _timeZoneService.Now);

            LastProjection = projection;
            return projection;
        }

        /// <summary>
        /// Pure core of <see cref="ProjectAsync"/>: no clock, no database. Exposed so the
        /// accounting rules can be unit-tested directly.
        /// </summary>
        public static CostBasisProjection Project(
            IReadOnlyList<CostBasisLayerInfo> seedLayers,
            IReadOnlyList<ProjectionStep> steps,
            double currentSocWh,
            double chargeEfficiency,
            double dischargeEfficiency,
            DateTime builtAt)
        {
            // A zero or negative efficiency is bad data, not a battery — divide by 1 instead.
            if (chargeEfficiency <= 0.0) chargeEfficiency = 1.0;
            if (dischargeEfficiency <= 0.0) dischargeEfficiency = 1.0;

            var layers = new LinkedList<ChargeLayer>();
            foreach (var l in seedLayers)
                layers.AddLast(new ChargeLayer { Wh = l.Wh, CostEurPerKWh = l.CostEurPerKWh });

            var quarters = new List<ProjectedQuarterCost>(steps.Count);
            double gridChargeKWh = 0.0;
            double gridChargeCostEur = 0.0;
            double prevSoc = currentSocWh;

            foreach (var step in steps)
            {
                double delta = step.SocWh - prevSoc;
                prevSoc = step.SocWh;

                if (delta > MinLayerWh)
                {
                    // Storing delta requires buying delta/chEff on the AC side.
                    double acWh = delta / chargeEfficiency;
                    var (freeAcWh, gridAcWh) = SplitChargeBySurplus(acWh, step.SolarSurplusWh);

                    PushCharge(layers,
                        freeAcWh * chargeEfficiency,
                        gridAcWh * chargeEfficiency,
                        Stored(step.BuyEurPerKWh, chargeEfficiency));

                    gridChargeKWh += gridAcWh / 1000.0;
                    gridChargeCostEur += gridAcWh / 1000.0 * step.BuyEurPerKWh;
                }
                else if (delta < -MinLayerWh)
                {
                    // The delta is already stored energy, so no efficiency correction here.
                    RemoveOldest(layers, -delta);
                }

                var (totalWh, totalCost) = Totals(layers);
                double avg = totalWh > MinLayerWh ? totalCost / (totalWh / 1000.0) : 0.0;

                quarters.Add(new ProjectedQuarterCost(
                    step.Time, totalWh, avg, Delivered(avg, dischargeEfficiency)));
            }

            var (endWh, endCost) = Totals(layers);
            double endAvg = endWh > MinLayerWh ? endCost / (endWh / 1000.0) : 0.0;

            return new CostBasisProjection(
                BuiltAt: builtAt,
                Quarters: quarters,
                EndOfHorizonLayers: Describe(layers, dischargeEfficiency),
                PlannedGridChargeKWh: gridChargeKWh,
                PlannedGridChargeCostEur: gridChargeCostEur,
                PlannedAverageBuyEurPerKWh: gridChargeKWh > 0.0 ? gridChargeCostEur / gridChargeKWh : 0.0,
                EndOfHorizonStoredEurPerKWh: endAvg,
                EndOfHorizonDeliveredEurPerKWh: Delivered(endAvg, dischargeEfficiency));
        }

        // ── Measured history ─────────────────────────────────────────────────

        /// <summary>
        /// Ensures the FIFO queue reflects all measurements up to now.
        /// On first call it rebuilds from history; subsequent calls fold in only
        /// the measurements that arrived since the last processed time.
        /// </summary>
        private async Task EnsureUpToDateAsync()
        {
            // Fetched outside the lock: it queries the DB and never re-enters this service.
            await RefreshEfficienciesAsync().ConfigureAwait(false);

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

        /// <summary>Refreshes the cached one-way efficiencies when they are stale.</summary>
        private async Task RefreshEfficienciesAsync()
        {
            var now = _timeZoneService.Now;
            if (now - _efficienciesFetchedAt < EfficiencyCacheTime)
                return;

            try
            {
                var (charge, discharge) = await _batteryEfficiencyService
                    .GetEfficienciesAsync().ConfigureAwait(false);

                if (charge > 0.0 && discharge > 0.0)
                    _efficiencies = (charge, discharge);

                _efficienciesFetchedAt = now;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"ChargeCostBasis efficiency lookup failed: {ex.Message}");
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
            var gridImportByTime = await LoadGridImportAsync(
                replaySlice.First().Time, replaySlice.Last().Time).ConfigureAwait(false);
            var prices = await _calculationService
                .CalculateEnergyPricesBatchAsync(replaySlice.Select(m => m.Time))
                .ConfigureAwait(false);

            foreach (var m in replaySlice)
                ApplyMeasurement(m, gridImportByTime, prices);

            // BatteryPowerWatts is AC-side and the layers are stored-side, so the conversion
            // uses the fitted efficiency and drifts slowly from the DC-side measured SOC.
            // Resync the queue total to the last measured SOC: trim oldest layers if over
            // (already consumed), or pad with a free layer if under (unknown origin).
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

            var gridImportByTime = await LoadGridImportAsync(
                measurements.First().Time, measurements.Last().Time).ConfigureAwait(false);
            var prices = await _calculationService
                .CalculateEnergyPricesBatchAsync(measurements.Select(m => m.Time))
                .ConfigureAwait(false);

            foreach (var m in measurements)
                ApplyMeasurement(m, gridImportByTime, prices);

            ResyncToMeasuredSoc(measurements.Last().BatteryStateOfChargeWh);

            _lastProcessedTime = measurements.Last().Time;
        }

        /// <summary>
        /// Applies a single quarter measurement to the FIFO queue: charging pushes layers
        /// (the part that came off the meter at the buying price, the rest free), discharging
        /// pops the oldest energy first.
        /// </summary>
        private void ApplyMeasurement(
            QuarterlyMeasurement m,
            IReadOnlyDictionary<DateTime, double> gridImportByTime,
            IReadOnlyDictionary<DateTime, EnergyPrice> prices)
        {
            double chargedWh = m.BatteryChargedKWh * 1000.0;
            double dischargedWh = m.BatteryDischargedKWh * 1000.0;

            double chEff = _efficiencies.Charge;
            double disEff = _efficiencies.Discharge;

            if (chargedWh > MinLayerWh)
            {
                double buyPrice = prices.TryGetValue(m.Time, out var p) ? p.Buying : 0.0;

                // Only what actually crossed the meter is paid for; the rest was solar surplus
                // that never left the house. Mirrors MeasurementView.GridChargeCostEur.
                double importWh = gridImportByTime.TryGetValue(m.Time, out var g) ? g : 0.0;

                var (freeAcWh, gridAcWh) = SplitChargeByMeter(chargedWh, importWh);

                PushCharge(_layers, freeAcWh * chEff, gridAcWh * chEff, Stored(buyPrice, chEff));
            }
            else if (dischargedWh > MinLayerWh)
            {
                RemoveOldest(_layers, StoredDrainFor(dischargedWh, disEff));
            }
        }

        /// <summary>
        /// Reconciles the FIFO queue total with the measured DC-side SOC.
        /// Over-tracking trims the oldest layers — those were effectively already consumed.
        /// Under-tracking pads a free layer at the back.
        /// </summary>
        private void ResyncToMeasuredSoc(double measuredSocWh)
        {
            if (measuredSocWh < 0.0) measuredSocWh = 0.0;

            double tracked = _layers.Sum(l => l.Wh);
            double diff = tracked - measuredSocWh;

            if (diff > MinLayerWh)
            {
                // Over-tracked: remove the oldest energy first (FIFO).
                RemoveOldest(_layers, diff);
            }
            else if (diff < -MinLayerWh)
            {
                // Under-tracked: pad with a free layer of unknown origin.
                _layers.AddLast(new ChargeLayer { Wh = -diff, CostEurPerKWh = 0.0 });
            }
        }

        // ── FIFO primitives, shared by the measured and the projected branch ─

        /// <summary>
        /// Splits a measured charge into its free and its paid part using what actually crossed
        /// the meter. Total solar production is the wrong signal — solar the house consumed
        /// itself was never available to the battery.
        /// </summary>
        public static (double FreeAcWh, double GridAcWh) SplitChargeByMeter(
            double chargedAcWh, double gridImportWh)
        {
            double grid = Math.Min(chargedAcWh, Math.Max(gridImportWh, 0.0));
            return (Math.Max(chargedAcWh - grid, 0.0), grid);
        }

        /// <summary>
        /// Splits a planned charge into its free and its paid part using the solar surplus
        /// forecast for that quarter. Forward-looking counterpart of <see cref="SplitChargeByMeter"/>.
        /// </summary>
        public static (double FreeAcWh, double GridAcWh) SplitChargeBySurplus(
            double chargedAcWh, double solarSurplusWh)
        {
            double free = Math.Min(chargedAcWh, Math.Max(solarSurplusWh, 0.0));
            return (free, Math.Max(chargedAcWh - free, 0.0));
        }

        /// <summary>Stored (DC) Wh that has to be drained to deliver <paramref name="deliveredAcWh"/>.</summary>
        public static double StoredDrainFor(double deliveredAcWh, double dischargeEfficiency)
            => dischargeEfficiency > 0.0 ? deliveredAcWh / dischargeEfficiency : deliveredAcWh;

        /// <summary>Pushes the free and the grid part of one charge onto the back of the queue.</summary>
        private static void PushCharge(
            LinkedList<ChargeLayer> layers,
            double freeStoredWh,
            double gridStoredWh,
            double gridCostPerStoredKWh)
        {
            if (freeStoredWh > MinLayerWh)
                layers.AddLast(new ChargeLayer { Wh = freeStoredWh, CostEurPerKWh = 0.0 });
            if (gridStoredWh > MinLayerWh)
                layers.AddLast(new ChargeLayer { Wh = gridStoredWh, CostEurPerKWh = gridCostPerStoredKWh });
        }

        /// <summary>Removes the requested stored Wh from the front (oldest) of the queue.</summary>
        private static void RemoveOldest(LinkedList<ChargeLayer> layers, double wh)
        {
            double remaining = wh;
            while (remaining > MinLayerWh && layers.First != null)
            {
                var layer = layers.First.Value;

                // Anything at or below the noise floor goes entirely — otherwise rounding
                // leaves dust layers behind that never drain and skew the average.
                if (layer.Wh - remaining <= MinLayerWh)
                {
                    remaining -= layer.Wh;
                    layers.RemoveFirst();
                }
                else
                {
                    layer.Wh -= remaining;
                    remaining = 0.0;
                }
            }
        }

        /// <summary>Total stored Wh and total EUR held in the queue.</summary>
        private static (double Wh, double CostEur) Totals(LinkedList<ChargeLayer> layers)
        {
            double totalWh = 0.0, totalCost = 0.0;
            foreach (var l in layers)
            {
                totalWh += l.Wh;
                totalCost += l.Wh / 1000.0 * l.CostEurPerKWh;
            }
            return (totalWh, totalCost);
        }

        /// <summary>Renders the queue for display, oldest first.</summary>
        private static List<CostBasisLayerInfo> Describe(LinkedList<ChargeLayer> layers, double disEff)
        {
            var result = new List<CostBasisLayerInfo>(layers.Count);
            int idx = 0;
            foreach (var l in layers)
            {
                result.Add(new CostBasisLayerInfo(
                    idx++, l.Wh, l.CostEurPerKWh, Delivered(l.CostEurPerKWh, disEff), l.CostEurPerKWh < 0.0001));
            }
            return result;
        }

        /// <summary>Buying price per AC kWh → cost per stored kWh.</summary>
        public static double Stored(double buyEurPerKWh, double chargeEfficiency)
            => chargeEfficiency > 0.0 ? buyEurPerKWh / chargeEfficiency : buyEurPerKWh;

        /// <summary>Cost per stored kWh → cost per delivered kWh.</summary>
        public static double Delivered(double storedEurPerKWh, double dischargeEfficiency)
            => dischargeEfficiency > 0.0 ? storedEurPerKWh / dischargeEfficiency : storedEurPerKWh;

        // ── Data access ──────────────────────────────────────────────────────

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

        /// <summary>
        /// Grid import (Wh) per quarter in [from, to], as the delta between consecutive P1 meter
        /// readings and keyed on the END of the interval. One reading before the window is loaded
        /// so the first quarter has something to subtract from. Same derivation as
        /// EnergyStatisticsService.BuildMeasurementViewsAsync — the meter is the single source of
        /// truth for what was actually bought.
        /// </summary>
        private async Task<Dictionary<DateTime, double>> LoadGridImportAsync(DateTime from, DateTime to)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var dataService = scope.ServiceProvider.GetRequiredService<EnergyHistoryDataService>();

            var histories = await dataService.GetList(async set =>
            {
                var result = set
                    .Where(h => h.Time >= from.AddMinutes(-15) && h.Time <= to)
                    .OrderBy(h => h.Time)
                    .ToList();
                return await Task.FromResult(result);
            }).ConfigureAwait(false);

            // One implementation of the meter delta, in QuarterlyFactsService. This used to be a
            // second copy of it, and a third and a fourth lived in EnergyStatisticsService and
            // GridPower — one of which did not clamp negative deltas.
            return QuarterlyFactsService.GridDeltas(histories)
                .ToDictionary(kv => kv.Key, kv => kv.Value.importWh);
        }
    }
}
