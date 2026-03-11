using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;
using static SessyData.Model.SessyWebControl;

namespace SessyController.Services
{
    /// <summary>
    /// Solver-based batteries controller with explicit support for:
    /// - day-ahead arbitrage when netting is enabled
    /// - self-consumption-first behavior when netting is disabled
    /// - solar surplus capture
    /// - runtime guards for full/empty batteries
    /// </summary>
    public sealed class BatteriesService : BackgroundHeartbeatService
    {
        private readonly LoggingService<BatteriesService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        private readonly IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private readonly IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;

        private IDisposable? _settingsConfigSubscription;
        private IDisposable? _sessyBatteryConfigSubscription;

        private SettingsConfig _settingsConfig;
        private SessyBatteryConfig _sessyBatteryConfig;

        private readonly SemaphoreSlim _semaphore = new(1, 1);

        private IServiceScope _scope;

        private DayAheadMarketService _dayAheadMarketService;
        private SolarService _solarService;
        private BatteryContainer _batteryContainer;
        private TimeZoneService _timeZoneService;
        private ConsumptionMonitorService _consumptionMonitorService;
        private SessyWebControlDataService _sessyWebControlDataService;
        private PerformanceDataService? _performanceDataService;
        private TaxesDataService _taxesDataService;

        private static List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();

        // Per-quarter policy context
        private Dictionary<DateTime, bool> _nettingByTime = new();
        private Dictionary<DateTime, double> _reserveWhByTime = new();
        private Dictionary<DateTime, double> _futureSelfUseValueByTime = new();

        // Rebuild throttling
        private DateTime? _lastPlannedQuarter;
        private int? _lastPriceSignature;

        public bool IsManualOverride => _settingsConfig.ManualOverride;
        public bool WeAreInControl { get; private set; } = true;

        public delegate Task DataChangedDelegate();
        public event DataChangedDelegate? DataChanged;

        private sealed record PlanAction
        {
            public Modes Mode;
            public double PowerW;
        }

        // Runtime / planning thresholds (Wh-like)
        private const double ReserveWh = 0.0;
        private const double EmptyHysteresisWh = 50.0;
        private const double FullThresholdRatio = 0.995; // 99.5% is practically full
        private const double NumericEpsWh = 0.001;

        public BatteriesService(
            LoggingService<BatteriesService> logger,
            IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
            IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;

            _settingsConfigMonitor = settingsConfigMonitor;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;

            _settingsConfig = settingsConfigMonitor.CurrentValue ?? throw new InvalidOperationException("ManagementSettings missing");
            _sessyBatteryConfig = sessyBatteryConfigMonitor.CurrentValue ?? throw new InvalidOperationException("Sessy:Batteries missing");

            _settingsConfigSubscription = _settingsConfigMonitor.OnChange(settings => _settingsConfig = settings);
            _sessyBatteryConfigSubscription = _sessyBatteryConfigMonitor.OnChange(settings => _sessyBatteryConfig = settings);

            _scope = _serviceScopeFactory.CreateScope();

            _dayAheadMarketService = _scope.ServiceProvider.GetRequiredService<DayAheadMarketService>();
            _solarService = _scope.ServiceProvider.GetRequiredService<SolarService>();
            _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _consumptionMonitorService = _scope.ServiceProvider.GetRequiredService<ConsumptionMonitorService>();
            _sessyWebControlDataService = _scope.ServiceProvider.GetRequiredService<SessyWebControlDataService>();
            _performanceDataService = _scope.ServiceProvider.GetService<PerformanceDataService>();
            _taxesDataService = _scope.ServiceProvider.GetRequiredService<TaxesDataService>();

            _logger.LogInformation("BatteriesService (solver-based, self-consumption aware) starting");
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _scope.Dispose();
            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("BatteriesService started ...");

            var delaySeconds = 60;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await HeartBeatAsync().ConfigureAwait(false);

                    await Process(cancellationToken).ConfigureAwait(false);

                    delaySeconds = DataChanged == null ? 1 : 60;

                    if (DataChanged != null)
                        await DataChanged.Invoke().ConfigureAwait(false);

                    await StorePerformance().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"An error occurred while managing batteries. {ex.ToDetailedString()}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation during delay.
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay. {ex.ToDetailedString()}");
                }
            }

            _logger.LogWarning("BatteriesService stopped.");
        }

        public async Task Process(CancellationToken cancellationToken)
        {
            if (_dayAheadMarketService == null || !_dayAheadMarketService.IsInitialized())
                return;

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!await RefreshQuarterlyPrices().ConfigureAwait(false))
                    return;

                await _consumptionMonitorService.EstimateConsumptionInWattsPerQuarter(_quarterlyInfos).ConfigureAwait(false);
                await _solarService.GetExpectedSolarPower(_quarterlyInfos).ConfigureAwait(false);

                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                await RebuildPlanIfNeeded(nowQuarter).ConfigureAwait(false);

                await BuildTariffContextAsync().ConfigureAwait(false);

                // Economic and policy post-processing
                ApplyCycleCostToPlan();
                ApplySelfConsumptionPolicy();

                // Physical feasibility repair
                await EnsureEnergyForPlannedDischargeAsync().ConfigureAwait(false);

                // Rebuild context once more because reserve/self-use value depends on final plan shape
                await BuildTariffContextAsync().ConfigureAwait(false);

                WritePlanIntoQuarterlyInfos();
                await WriteBackSocSimulationAsync().ConfigureAwait(false);

                if (await WeControlTheBatteries().ConfigureAwait(false))
                {
                    var executable = await GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

                    if (!_planByTime.TryGetValue(nowQuarter, out var plannedNow) ||
                        plannedNow.Mode != executable.Mode ||
                        Math.Abs(plannedNow.PowerW - executable.PowerW) > 0.1)
                    {
                        ApplyRuntimeOverrideToPlan(nowQuarter, executable);

                        await BuildTariffContextAsync().ConfigureAwait(false);
                        WritePlanIntoQuarterlyInfos();
                        await WriteBackSocSimulationAsync().ConfigureAwait(false);
                    }

                    await ExecuteAction(executable).ConfigureAwait(false);
                }
                else
                {
#if !DEBUG
                    await _batteryContainer.StopAll().ConfigureAwait(false);
#endif
                }
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"Unhandled exception in Process: {ex.ToDetailedString()}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<bool> RefreshQuarterlyPrices()
        {
            var prices = await _dayAheadMarketService.GetPrices().ConfigureAwait(false);

            _quarterlyInfos = prices
                .OrderBy(p => p.Time)
                .ToList();

            QuarterlyInfo.AddSmoothedPrices(_quarterlyInfos, 6);

            return _quarterlyInfos.Count > 0;
        }

        /// <summary>
        /// Reads the tax context per quarter and derives:
        /// - whether netting applies
        /// - reserve floor needed for later self-consumption
        /// - realistic future self-use value of stored energy
        /// </summary>
        private async Task BuildTariffContextAsync()
        {
            _nettingByTime.Clear();
            _reserveWhByTime.Clear();
            _futureSelfUseValueByTime.Clear();

            if (_quarterlyInfos.Count == 0) return;

            var ordered = _quarterlyInfos.OrderBy(q => q.Time).ToList();
            var taxesCacheByDate = new Dictionary<DateTime, Taxes?>();

            foreach (var qi in ordered)
            {
                var day = qi.Time.Date;

                if (!taxesCacheByDate.TryGetValue(day, out var taxes))
                {
                    taxes = await _taxesDataService.GetTaxesForDate(qi.Time).ConfigureAwait(false);
                    taxesCacheByDate[day] = taxes;
                }

                _nettingByTime[qi.Time] = taxes?.Netting ?? true;
            }

            double capWh = _batteryContainer.GetTotalCapacity();

            static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

            // 1) Reserve floor:
            // Keep only the amount that is realistically needed for later own use.
            double requiredWh = 0.0;

            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                var qi = ordered[i];
                bool netting = _nettingByTime[qi.Time];

                if (netting)
                {
                    _reserveWhByTime[qi.Time] = ReserveWh;
                    requiredWh = 0.0;
                    continue;
                }

                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                if (netLoadWh > 0.0)
                {
                    requiredWh += netLoadWh;
                }
                else
                {
                    requiredWh = Math.Max(ReserveWh, requiredWh + netLoadWh);
                }

                requiredWh = Clamp(requiredWh, ReserveWh, capWh);
                _reserveWhByTime[qi.Time] = requiredWh;
            }

            // 2) Self-use value:
            // Use an average future buying price over positive net-load quarters in a rolling horizon,
            // instead of the maximum future buy price.
            int horizonQuarters = 48; // 12 hours

            for (int i = 0; i < ordered.Count; i++)
            {
                var qi = ordered[i];
                bool netting = _nettingByTime[qi.Time];

                if (netting)
                {
                    _futureSelfUseValueByTime[qi.Time] = qi.BuyingPrice;
                    continue;
                }

                var futureWindow = ordered
                    .Skip(i)
                    .Take(horizonQuarters)
                    .Where(q => (q.EstimatedConsumptionPerQuarterInWatts - q.SolarPowerPerQuarterInWatts) > 0.0)
                    .ToList();

                double selfUseValue = futureWindow.Count == 0
                    ? qi.BuyingPrice
                    : futureWindow.Average(q => q.BuyingPrice);

                _futureSelfUseValueByTime[qi.Time] = selfUseValue;
            }
        }

        /// <summary>
        /// Rebuild plan only when needed:
        /// - first time
        /// - new quarter
        /// - price/signature changed
        /// Keeps previous plan when MILP does not return a usable plan.
        /// </summary>
        private async Task<bool> RebuildPlanIfNeeded(DateTime nowQuarter)
        {
            int currentSignature = CalculatePriceSignature(_quarterlyInfos);

            bool needRebuild =
                _planByTime.Count == 0 ||
                _lastPlannedQuarter == null ||
                _lastPlannedQuarter.Value != nowQuarter ||
                _lastPriceSignature == null ||
                _lastPriceSignature.Value != currentSignature;

            if (!needRebuild)
                return false;

            bool built = BuildMilpPlan();

            if (!built)
            {
                _logger.LogWarning("MILP solve did not produce a usable plan. Keeping previous plan.");
                return false;
            }

            _lastPlannedQuarter = nowQuarter;
            _lastPriceSignature = currentSignature;

            return true;
        }

        private static int CalculatePriceSignature(List<QuarterlyInfo> infos)
        {
            unchecked
            {
                int hash = 17;

                foreach (var q in infos.OrderBy(x => x.Time))
                {
                    hash = hash * 23 + q.Time.GetHashCode();
                    hash = hash * 23 + Math.Round(q.BuyingPrice, 4).GetHashCode();
                    hash = hash * 23 + Math.Round(q.SellingPrice, 4).GetHashCode();
                    hash = hash * 23 + Math.Round(q.EstimatedConsumptionPerQuarterInWatts, 1).GetHashCode();
                    hash = hash * 23 + Math.Round(q.SolarPowerPerQuarterInWatts, 1).GetHashCode();
                }

                return hash;
            }
        }

        private bool BuildMilpPlan()
        {
            try
            {
                double capacityWh = _batteryContainer.GetTotalCapacity();
                double capacityKWh = capacityWh / 1000.0;

                double socWh = _batteryContainer.GetStateOfChargeInWatts().GetAwaiter().GetResult();
                double socKWh = socWh / 1000.0;

                double maxChargeKW = _sessyBatteryConfig.TotalChargingCapacity / 1000.0;
                double maxDischargeKW = _sessyBatteryConfig.TotalDischargingCapacity / 1000.0;

                var pricePoints = _quarterlyInfos
                    .OrderBy(q => q.Time)
                    .Select(q =>
                    {
                        double netLoadWh = q.EstimatedConsumptionPerQuarterInWatts - q.SolarPowerPerQuarterInWatts;

                        return new PricePoint(
                            q.Time,
                            q.BuyingPrice,
                            q.SellingPrice,
                            netLoadWh
                        );
                    })
                    .ToList();

                var spec = new BatterySpec(
                    CapacityKWh: capacityKWh,
                    InitialSocKWh: socKWh,
                    MinSocKWh: 0.0,
                    MaxSocKWh: capacityKWh,
                    MaxChargeKW: maxChargeKW,
                    MaxDischargeKW: maxDischargeKW,
                    ChargeEfficiency: 0.95,
                    DischargeEfficiency: 0.95
                );

                var opt = new SessyOptions(
                    QuarterMinutes: 15,
                    ActiveQuarterPenaltyEur: 0.01,
                    ForbidSimultaneousChargeDischarge: true,
                    TimeLimitMs: 500
                );

                var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt);

                if (result == null || result.Plan == null || result.Plan.Count == 0)
                    return false;

                var newPlan = new Dictionary<DateTime, PlanAction>(result.Plan.Count);

                foreach (var p in result.Plan)
                {
                    double powerW;
                    ChargingModes.Modes mode;

                    switch (p.Mode)
                    {
                        case ActionMode.Charge:
                            mode = ChargingModes.Modes.Charging;
                            powerW = Math.Round(p.ChargeKW * 1000.0, 0);
                            break;

                        case ActionMode.Discharge:
                            mode = ChargingModes.Modes.Discharging;
                            powerW = Math.Round(p.DischargeKW * 1000.0, 0);
                            break;

                        default:
                            mode = ChargingModes.Modes.ZeroNetHome;
                            powerW = 0.0;
                            break;
                    }

                    newPlan[p.Start] = new PlanAction
                    {
                        Mode = mode,
                        PowerW = powerW
                    };
                }

                foreach (var qi in _quarterlyInfos)
                {
                    if (!newPlan.ContainsKey(qi.Time))
                        newPlan[qi.Time] = new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
                }

                _planByTime = newPlan;
                _logger.LogInformation($"MILP plan built: optimal={result.Optimal}, objective={result.ObjectiveEur:F4} EUR");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"BuildMilpPlan failed: {ex.ToDetailedString()}");
                return false;
            }
        }

        /// <summary>
        /// Removes obviously unprofitable discharge quarters.
        /// </summary>
        private void ApplyCycleCostToPlan()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0) return;

            double minSpread = _settingsConfig.CycleCost;
            if (minSpread <= 0.0) return;

            double? minBuyOfPlannedChargeSoFar = null;

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time))
            {
                var act = _planByTime[qi.Time];

                if (act.Mode == ChargingModes.Modes.Charging && act.PowerW > 10)
                {
                    minBuyOfPlannedChargeSoFar = minBuyOfPlannedChargeSoFar == null
                        ? qi.BuyingPrice
                        : Math.Min(minBuyOfPlannedChargeSoFar.Value, qi.BuyingPrice);

                    continue;
                }

                if (act.Mode == ChargingModes.Modes.Discharging && act.PowerW > 10)
                {
                    double basis = minBuyOfPlannedChargeSoFar ?? qi.BuyingPrice;
                    double spread = qi.SellingPrice - basis;

                    if (spread < minSpread)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                    }
                }
            }
        }

        /// <summary>
        /// When netting is disabled:
        /// - capture solar surplus
        /// - only discharge if selling is clearly better than keeping energy for future self-use
        /// </summary>
        private void ApplySelfConsumptionPolicy()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0) return;

            double maxChargeW = _batteryContainer.GetChargingCapacityInWattsPerHour();

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time))
            {
                if (!_nettingByTime.TryGetValue(qi.Time, out var netting) || netting)
                    continue;

                var act = _planByTime[qi.Time];
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                // Capture solar surplus first
                if (netLoadWh < -1.0)
                {
                    act.Mode = ChargingModes.Modes.Charging;
                    act.PowerW = maxChargeW;
                    continue;
                }

                if (act.Mode == ChargingModes.Modes.Discharging)
                {
                    double selfUseValue = _futureSelfUseValueByTime.TryGetValue(qi.Time, out var v)
                        ? v
                        : qi.BuyingPrice;

                    double dischargeThreshold = selfUseValue + _settingsConfig.CycleCost;

                    if (qi.SellingPrice < dischargeThreshold)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Ensure the final plan is physically feasible:
        /// - no charging when practically full
        /// - no discharging below reserve floor
        /// - if a discharge is infeasible, first try to add an earlier suitable charging slot
        /// </summary>
        private async Task EnsureEnergyForPlannedDischargeAsync()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0) return;

            var now = _timeZoneService.Now.DateFloorQuarter();

            double capWh = _batteryContainer.GetTotalCapacity();
            double startSocWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

            var future = _quarterlyInfos
                .OrderBy(q => q.Time)
                .Where(q => q.Time >= now)
                .ToList();

            if (future.Count == 0) return;

            foreach (var qi in future)
            {
                if (!_planByTime.ContainsKey(qi.Time))
                    _planByTime[qi.Time] = new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
            }

            const int maxIterations = 400;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                var socBefore = new Dictionary<DateTime, double>(future.Count);
                double soc = Clamp(startSocWh, 0.0, capWh);

                DateTime? violationAt = null;

                foreach (var qi in future)
                {
                    socBefore[qi.Time] = soc;

                    var act = _planByTime[qi.Time];
                    double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                    double reserveFloorWh = _reserveWhByTime.TryGetValue(qi.Time, out var floor) ? floor : ReserveWh;

                    if (act.Mode == ChargingModes.Modes.Charging)
                    {
                        if (soc >= fullThresholdWh || soc + chargeStepWh >= fullThresholdWh)
                        {
                            act.Mode = ChargingModes.Modes.ZeroNetHome;
                            act.PowerW = 0;
                        }
                        else
                        {
                            soc = Math.Min(capWh, soc + chargeStepWh);
                        }
                    }
                    else if (act.Mode == ChargingModes.Modes.Discharging)
                    {
                        if (soc - dischargeStepWh < reserveFloorWh + EmptyHysteresisWh + NumericEpsWh)
                        {
                            violationAt = qi.Time;
                            break;
                        }

                        soc = Math.Max(0.0, soc - dischargeStepWh);
                    }

                    soc = Clamp(soc - netLoadWh, 0.0, capWh);
                }

                if (violationAt == null)
                    return;

                var candidates = future
                    .Where(q => q.Time < violationAt.Value)
                    .Select(q => new
                    {
                        Qi = q,
                        Act = _planByTime[q.Time],
                        SocBefore = socBefore.TryGetValue(q.Time, out var s) ? s : startSocWh,
                        NetLoadWh = q.EstimatedConsumptionPerQuarterInWatts - q.SolarPowerPerQuarterInWatts
                    })
                    .Where(x => x.Act.Mode != ChargingModes.Modes.Discharging)
                    .Where(x => x.Act.Mode != ChargingModes.Modes.Charging)
                    .Where(x => x.SocBefore + chargeStepWh < fullThresholdWh)
                    .OrderBy(x => x.NetLoadWh < 0 ? 0 : 1)
                    .ThenBy(x => x.Qi.BuyingPrice)
                    .ToList();

                if (candidates.Count > 0)
                {
                    var chosen = candidates[0];
                    chosen.Act.Mode = ChargingModes.Modes.Charging;
                    chosen.Act.PowerW = _batteryContainer.GetChargingCapacityInWattsPerHour();
                    continue;
                }

                var vAct = _planByTime[violationAt.Value];
                vAct.Mode = ChargingModes.Modes.ZeroNetHome;
                vAct.PowerW = 0;
            }

            _logger.LogWarning("EnsureEnergyForPlannedDischargeAsync: maxIterations reached; plan may still contain infeasible segments.");
        }

        private void WritePlanIntoQuarterlyInfos()
        {
            foreach (var qi in _quarterlyInfos)
            {
                if (!_planByTime.TryGetValue(qi.Time, out var act))
                {
                    qi.SetMode(ChargingModes.Modes.ZeroNetHome);
                    qi.SetPlanPower(0, 0);
                    continue;
                }

                qi.SetMode(act.Mode);

                if (act.Mode == ChargingModes.Modes.Charging)
                    qi.SetPlanPower(act.PowerW, 0);
                else if (act.Mode == ChargingModes.Modes.Discharging)
                    qi.SetPlanPower(0, act.PowerW);
                else
                    qi.SetPlanPower(0, 0);
            }
        }

        /// <summary>
        /// Simulate SOC from NOW forward and restore old ChargeNeeded semantics:
        /// - ChargeLeft = realized SOC after action + household effect
        /// - ChargeNeeded = target/floor:
        ///   * Charging => upper target
        ///   * Discharging => lower target/floor
        ///   * ZeroNetHome => reserve floor
        /// </summary>
        private async Task WriteBackSocSimulationAsync()
        {
            if (_quarterlyInfos.Count == 0) return;

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            double capWh = _batteryContainer.GetTotalCapacity();
            double soc = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

            foreach (var past in _quarterlyInfos.Where(q => q.Time < nowQuarter))
            {
                // Keep past values unchanged
            }

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time).Where(q => q.Time >= nowQuarter))
            {
                if (!_planByTime.TryGetValue(qi.Time, out var act))
                {
                    act = new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
                    _planByTime[qi.Time] = act;
                }

                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                double reserveFloorWh = _reserveWhByTime.TryGetValue(qi.Time, out var floor) ? floor : ReserveWh;

                double targetSocWh;

                if (act.Mode == ChargingModes.Modes.Charging)
                {
                    if (soc >= fullThresholdWh || soc + chargeStepWh >= fullThresholdWh)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = reserveFloorWh;
                    }
                    else
                    {
                        targetSocWh = Math.Min(capWh, Math.Max(soc, reserveFloorWh) + chargeStepWh);
                        soc = Math.Min(capWh, soc + chargeStepWh);
                    }
                }
                else if (act.Mode == ChargingModes.Modes.Discharging)
                {
                    if (soc - dischargeStepWh < reserveFloorWh + EmptyHysteresisWh + NumericEpsWh)
                    {
                        act.Mode = ChargingModes.Modes.ZeroNetHome;
                        act.PowerW = 0;
                        targetSocWh = reserveFloorWh;
                    }
                    else
                    {
                        targetSocWh = reserveFloorWh;
                        soc = Math.Max(0.0, soc - dischargeStepWh);
                    }
                }
                else
                {
                    targetSocWh = reserveFloorWh;
                }

                soc = Clamp(soc - netLoadWh, 0.0, capWh);

                qi.SetChargeNeeded(targetSocWh);
                qi.SetChargeLeft(soc);

                qi.SetMode(act.Mode);

                if (act.Mode == ChargingModes.Modes.Charging)
                    qi.SetPlanPower(act.PowerW > 0 ? act.PowerW : _batteryContainer.GetChargingCapacityInWattsPerHour(), 0);
                else if (act.Mode == ChargingModes.Modes.Discharging)
                    qi.SetPlanPower(0, act.PowerW > 0 ? act.PowerW : _batteryContainer.GetDischargingCapacityInWattsPerHour());
                else
                    qi.SetPlanPower(0, 0);
            }
        }

        /// <summary>
        /// Runtime-safe action for NOW:
        /// - no charge when practically full
        /// - no discharge below reserve floor
        /// - when netting is disabled and there is solar surplus, force charging
        /// - when netting is disabled, do not discharge if self-use value is better
        /// </summary>
        private async Task<PlanAction> GetExecutableActionForNowAsync(DateTime nowQuarter)
        {
            if (!_planByTime.TryGetValue(nowQuarter, out var planned))
            {
                return new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
            }

            double capWh = _batteryContainer.GetTotalCapacity();
            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            double chargeStepWh = _batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0;
            double dischargeStepWh = _batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0;
            double fullThresholdWh = capWh * FullThresholdRatio;

            bool netting = _nettingByTime.TryGetValue(nowQuarter, out var n) ? n : true;
            double reserveFloorWh = _reserveWhByTime.TryGetValue(nowQuarter, out var floor) ? floor : ReserveWh;
            double futureSelfUseValue = _futureSelfUseValueByTime.TryGetValue(nowQuarter, out var f) ? f : 0.0;

            var qi = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);

            if (!netting && qi != null)
            {
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;
                if (netLoadWh < -1.0 && socWh + chargeStepWh < fullThresholdWh)
                {
                    return new PlanAction
                    {
                        Mode = ChargingModes.Modes.Charging,
                        PowerW = _batteryContainer.GetChargingCapacityInWattsPerHour()
                    };
                }
            }

            if (planned.Mode == ChargingModes.Modes.Charging)
            {
                if (socWh >= fullThresholdWh || socWh + chargeStepWh >= fullThresholdWh)
                {
                    return new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
                }

                return planned;
            }

            if (planned.Mode == ChargingModes.Modes.Discharging)
            {
                double requiredWh = planned.PowerW > 10 ? planned.PowerW * 0.25 : dischargeStepWh;

                if (socWh - requiredWh < reserveFloorWh + EmptyHysteresisWh + NumericEpsWh)
                {
                    return new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
                }

                if (!netting && qi != null)
                {
                    double dischargeThreshold = futureSelfUseValue + _settingsConfig.CycleCost;

                    if (qi.SellingPrice < dischargeThreshold)
                    {
                        return new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
                    }
                }

                return planned;
            }

            return planned;
        }

        private void ApplyRuntimeOverrideToPlan(DateTime time, PlanAction action)
        {
            _planByTime[time] = action;
        }

        private async Task ExecuteAction(PlanAction action)
        {
#if !DEBUG
            if (_dayAheadMarketService.IsInitialized())
            {
                if (_settingsConfig.ManualOverride)
                {
                    await ExecuteManualOverride().ConfigureAwait(false);
                    return;
                }

                switch (action.Mode)
                {
                    case ChargingModes.Modes.Charging:
                        if (action.PowerW > 10)
                            await _batteryContainer.StartCharging((int)Math.Round(action.PowerW)).ConfigureAwait(false);
                        else
                            await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;

                    case ChargingModes.Modes.Discharging:
                        if (action.PowerW > 10)
                            await _batteryContainer.StartDisharging((int)Math.Round(action.PowerW)).ConfigureAwait(false);
                        else
                            await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;

                    case ChargingModes.Modes.ZeroNetHome:
                        await _batteryContainer.StartNetZeroHome().ConfigureAwait(false);
                        break;

                    case ChargingModes.Modes.Disabled:
                    default:
                        await _batteryContainer.StopAll().ConfigureAwait(false);
                        break;
                }
            }
            else
            {
                await ExecuteManualOverride().ConfigureAwait(false);
            }
#else
            await Task.Delay(1).ConfigureAwait(false);
#endif
        }

        private async Task ExecuteManualOverride()
        {
#if !DEBUG
            var localTime = _timeZoneService.Now;

            if (_settingsConfig.ManualChargingHours != null && _settingsConfig.ManualChargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartCharging(_batteryContainer.GetChargingCapacityInWattsPerHour()).ConfigureAwait(false);
            else if (_settingsConfig.ManualDischargingHours != null && _settingsConfig.ManualDischargingHours.Contains(localTime.Hour))
                await _batteryContainer.StartDisharging(_batteryContainer.GetDischargingCapacityInWattsPerHour()).ConfigureAwait(false);
            else if (_settingsConfig.ManualNetZeroHomeHours != null && _settingsConfig.ManualNetZeroHomeHours.Contains(localTime.Hour))
                await _batteryContainer.StartNetZeroHome().ConfigureAwait(false);
            else
                await _batteryContainer.StopAll().ConfigureAwait(false);
#else
            await Task.Delay(1).ConfigureAwait(false);
#endif
        }

        private async Task<bool> WeControlTheBatteries()
        {
            var supplierInControl = await SupplierIsControllingTheBatteries().ConfigureAwait(false);
            var chargedInControl = _settingsConfig.ChargedInControl;

            WeAreInControl = !(supplierInControl || chargedInControl);

            SessyWebControlStatus status = SessyWebControlStatus.SessyWeb;

            if (!WeAreInControl)
            {
                if (_settingsConfig.ChargedInControl)
                    status = SessyWebControlStatus.Charged;

                if (supplierInControl)
                    status = SessyWebControlStatus.Provider;
            }

            var last = await _sessyWebControlDataService.Get(async set =>
            {
                var result = set.OrderByDescending(sc => sc.Time).FirstOrDefault();
                return await Task.FromResult(result).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (last == null || last.Status != status)
                await StoreStatus(status).ConfigureAwait(false);

            return WeAreInControl;
        }

        private async Task StoreStatus(SessyWebControlStatus status)
        {
            var controlList = new List<SessyWebControl>
            {
                new SessyWebControl
                {
                    Time = _timeZoneService.Now,
                    Status = status
                }
            };

            await _sessyWebControlDataService.Add(controlList).ConfigureAwait(false);
        }

        private async Task<bool> SupplierIsControllingTheBatteries()
        {
            foreach (var battery in _batteryContainer.Batteries)
            {
                var currentPowerStrategy = await battery.GetPowerStatus().ConfigureAwait(false);
                if (currentPowerStrategy.Sessy.StrategyOverridden)
                    return true;
            }

            return false;
        }

        private async Task StorePerformance()
        {
            if (_performanceDataService == null) return;

            var currentQuarterlyInfo = GetNextQuarterlyInfoInPlan();

            if (currentQuarterlyInfo == null) return;

            bool exists = await _performanceDataService.Exists(async set =>
            {
                var result = set.Any(pd => pd.Time == currentQuarterlyInfo.Time);
                return await Task.FromResult(result).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (exists) return;

            var totalCapacity = _batteryContainer.GetTotalCapacity();

            var performanceData = new List<Performance>
            {
                new Performance
                {
                    Time = currentQuarterlyInfo.Time,
                    MarketPrice = currentQuarterlyInfo.MarketPrice,
                    BuyingPrice = currentQuarterlyInfo.BuyingPrice,
                    SmoothedBuyingPrice = currentQuarterlyInfo.SmoothedBuyingPrice,
                    SellingPrice = currentQuarterlyInfo.SellingPrice,
                    SmoothedSellingPrice = currentQuarterlyInfo.SmoothedSellingPrice,
                    Profit = currentQuarterlyInfo.Profit,
                    EstimatedConsumptionPerQuarterHour = currentQuarterlyInfo.EstimatedConsumptionPerQuarterInWatts,
                    ChargeLeft = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false),
                    ChargeNeeded = currentQuarterlyInfo.ChargeNeededWh,
                    Charging = currentQuarterlyInfo.Charging,
                    Discharging = currentQuarterlyInfo.Discharging,
                    ZeroNetHome = currentQuarterlyInfo.ZeroNetHome,
                    Disabled = currentQuarterlyInfo.Disabled,
                    SolarPowerPerQuarterHour = currentQuarterlyInfo.SolarPowerPerQuarterHour,
                    SmoothedSolarPower = currentQuarterlyInfo.SmoothedSolarPower,
                    SolarGlobalRadiation = currentQuarterlyInfo.SolarGlobalRadiation,
                    ChargeLeftPercentage = currentQuarterlyInfo.ChargeLeftPercentage(totalCapacity),
                    DisplayState = currentQuarterlyInfo.GetDisplayMode(),
                    VisualizeInChart = currentQuarterlyInfo.VisualizeInChart(),
                }
            };

            await _performanceDataService.Add(performanceData).ConfigureAwait(false);
        }

        public List<QuarterlyInfo> GetQuarterlyInfos()
        {
            _semaphore.Wait();
            try
            {
                return _quarterlyInfos.ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> GetBatteryMode()
        {
            var now = _timeZoneService.Now.DateFloorQuarter();
            if (_planByTime.TryGetValue(now, out var act))
                return ChargingModes.GetDisplayMode(act.Mode);

            return "???";
        }

        public QuarterlyInfo? GetNextQuarterlyInfoInPlan()
        {
            var now = _timeZoneService.Now.DateFloorQuarter();
            return _quarterlyInfos
                .OrderBy(q => q.Time)
                .FirstOrDefault(q => q.Time >= now && _planByTime.ContainsKey(q.Time));
        }

        public async Task<double> getBatteryPercentage()
        {
            return await _batteryContainer.GetBatterPercentage().ConfigureAwait(false);
        }

        private bool _isDisposed;

        public override void Dispose()
        {
            if (_isDisposed) return;

            _settingsConfigSubscription?.Dispose();
            _sessyBatteryConfigSubscription?.Dispose();

            _quarterlyInfos.Clear();
            _planByTime.Clear();
            _nettingByTime.Clear();
            _reserveWhByTime.Clear();
            _futureSelfUseValueByTime.Clear();

            _scope.Dispose();

            base.Dispose();
            _isDisposed = true;
        }
    }
}