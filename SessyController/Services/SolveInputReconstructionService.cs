using System.Text.Json;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Rebuilds a planner solve-input for a past period from the database, so a plan that ran
    /// before solve-input recording was switched on can still be replayed.
    ///
    /// It is approximate on purpose, and the UI says so. Prices, net load and the reserve floor
    /// come straight from the stored PlannedQuarter rows, and the initial SOC from the measured
    /// ActualQuarter — those are exact. The battery spec (capacity, efficiency, taper) and the
    /// planner options are the CURRENT ones, not as they were then, because those are learned
    /// values that are not snapshotted per quarter. Good enough to find a greedy-vs-DP divergence,
    /// not bit-exact.
    /// </summary>
    public class SolveInputReconstructionService
    {
        private readonly PlannedQuarterDataService _plannedService;
        private readonly ActualQuarterDataService _actualService;
        private readonly BatteryContainer _batteryContainer;
        private readonly BatteryEfficiencyService _efficiencyService;
        private readonly ThrottleAnalysisService _throttleService;
        private readonly IOptionsMonitor<SessyBatteryConfig> _batteryConfigMonitor;
        private readonly SettingsService _settingsService;
        private readonly ReplacementCostService _replacementCostService;
        private readonly ChargeCostBasisService _chargeCostBasisService;

        public SolveInputReconstructionService(
            PlannedQuarterDataService plannedService,
            ActualQuarterDataService actualService,
            BatteryContainer batteryContainer,
            BatteryEfficiencyService efficiencyService,
            ThrottleAnalysisService throttleService,
            IOptionsMonitor<SessyBatteryConfig> batteryConfigMonitor,
            SettingsService settingsService,
            ReplacementCostService replacementCostService,
            ChargeCostBasisService chargeCostBasisService)
        {
            _plannedService = plannedService;
            _actualService = actualService;
            _batteryContainer = batteryContainer;
            _efficiencyService = efficiencyService;
            _throttleService = throttleService;
            _batteryConfigMonitor = batteryConfigMonitor;
            _settingsService = settingsService;
            _replacementCostService = replacementCostService;
            _chargeCostBasisService = chargeCostBasisService;
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// Reconstructs the solve-input for [start, end] and returns it as pretty-printed JSON,
        /// in the same shape SolveInputRecorder writes, so it replays through the same tooling.
        /// </summary>
        public async Task<string> BuildJsonAsync(DateTime start, DateTime end)
        {
            var planned = await _plannedService.GetList(async set =>
                await Task.FromResult(set.Where(p => p.Time >= start && p.Time <= end)
                                         .OrderBy(p => p.Time).ToList()));

            if (planned.Count == 0)
                throw new InvalidOperationException(
                    $"No planned quarters found between {start:yyyy-MM-dd HH:mm} and {end:yyyy-MM-dd HH:mm}.");

            // Initial SOC: the measured SOC at the first quarter; fall back to the plan's own
            // reference if no measurement was recorded for it.
            var firstTime = planned[0].Time;
            var actual = await _actualService.GetList(async set =>
                await Task.FromResult(set.Where(a => a.Time == firstTime).ToList()));
            double initialSocWh = actual.FirstOrDefault()?.ActualSocWh ?? planned[0].PlannedChargeLeftWh;

            var cfg = _batteryConfigMonitor.CurrentValue;
            double capacityWh = _batteryContainer.GetTotalCapacity();
            double capKWh = capacityWh / 1000.0;
            double maxChargeKW = cfg.TotalRawChargingCapacity / 1000.0;
            double maxDischargeKW = cfg.TotalRawDischargingCapacity / 1000.0;

            var (chargeEfficiency, dischargeEfficiency) = await _efficiencyService.GetEfficienciesAsync();
            var efficiencyCurve = await _efficiencyService.GetEfficiencyCurveAsync();
            var chargeTaper = await _throttleService.GetChargeTaperAsync();
            var dischargeCapability = await _throttleService
                .GetDischargeCapabilityAsync(cfg.TotalRawDischargingCapacity);
            var chargeFloor = await _throttleService
                .GetChargeCapabilityFloorAsync(cfg.TotalRawChargingCapacity);
            var chargeCapability = await _throttleService
                .GetChargeCapabilityAsync(cfg.TotalRawChargingCapacity);

            var spec = new BatterySpec(
                CapacityKWh: capKWh,
                InitialSocKWh: initialSocWh / 1000.0,
                MaxChargeKW: maxChargeKW,
                MaxDischargeKW: maxDischargeKW,
                ChargeEfficiency: chargeEfficiency,
                DischargeEfficiency: dischargeEfficiency,
                ChargeTaper: chargeTaper,
                Efficiency: efficiencyCurve,
                DischargeCapability: dischargeCapability,
                ChargeFloor: chargeFloor,
                ChargeCapability: chargeCapability);

            double replacementCost = await _replacementCostService.GetReplacementCostAsync();
            if (replacementCost <= 0.0)
            {
                var costBasis = await _chargeCostBasisService.GetSnapshotAsync();
                replacementCost = Math.Max(0.0, costBasis.AverageCostBasisEur);
            }

            var s = _settingsService.Current;
            var opt = new SessyOptions(
                QuarterMinutes: 15,
                CycleCostEurPerKWh: _settingsService.CycleCost,
                FutureValueDiscountPerHour: s.FutureValueDiscountPerHour,
                ReservationPriceEurPerKWh: replacementCost,
                AllowCarryForward: s.CarryForwardEnabled);

            // Prices, net load and reserve floor are exact from the stored plan. Solar surplus is
            // the negative part of net load, exactly as the live planner derives it.
            var points = planned.Select(p => new PricePoint(
                p.Time,
                p.BuyingPriceEurKWh,
                p.SellingPriceEurKWh,
                p.NetLoadWh,
                p.NetLoadWh < 0.0 ? -p.NetLoadWh : 0.0)).ToList();

            var bounds = planned.Select(p => new SocBound(p.Time, p.MinSocWh / 1000.0, capKWh)).ToList();

            var input = new SolveInputRecorder.SolveInput(DateTime.Now, points, spec, opt, bounds);

            return JsonSerializer.Serialize(input, JsonOptions);
        }
    }
}
