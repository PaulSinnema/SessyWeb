using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization; // BatteryArbitrageMilp
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;
using static SessyData.Model.SessyWebControl;

namespace SessyController.Services
{
    /// <summary>
    /// Solver-based batteries controller:
    /// - Fetch day-ahead quarter prices
    /// - Enrich with expected consumption/solar
    /// - Build MILP plan (charge/discharge/net-zero)
    /// - Apply CycleCost gating
    /// - Repair SOC feasibility (no discharging when there isn't enough energy)
    /// - Write plan into QuarterlyInfos (for UI)
    /// - Simulate SOC forward from "now" (ChargeLeft/ChargeNeeded for UI)
    /// - Execute current quarter with a runtime SOC guard (and keep UI consistent)
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
        private static List<QuarterlyInfo> _quarterlyInfos = new();
        private Dictionary<DateTime, PlanAction> _planByTime = new();

        public bool IsManualOverride => _settingsConfig.ManualOverride;
        public bool WeAreInControl { get; private set; } = true;

        public delegate Task DataChangedDelegate();
        public event DataChangedDelegate? DataChanged;

        private sealed record PlanAction
        {
            public Modes Mode;
            public double PowerW; // planned power (W)
        }

        // Runtime safety buffers (Wh-like)
        private const double SocHysteresisWh = 50.0; // buffer to prevent flapping around 0
        private const double ReserveWh = 0.0;        // set >0 if you want a hard reserve (e.g. 500Wh)

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

            _logger.LogInformation("BatteriesService (solver-based) starting");
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _scope.Dispose();
            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("BatteriesService (solver-based) started ...");

            var delay = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await HeartBeatAsync().ConfigureAwait(false);

                    await Process(cancellationToken).ConfigureAwait(false);

                    delay = 60;

                    if (DataChanged == null)
                    {
                        delay = 1;
                    }
                    else
                    {
                        await DataChanged.Invoke().ConfigureAwait(false);
                    }

                    await StorePerformance().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"An error occurred while managing batteries.{ex.ToDetailedString()}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay, keep processing {ex.ToDetailedString()}");
                }
            }

            _logger.LogWarning("BatteriesService stopped.");
        }

        private async Task StorePerformance()
        {
            var currentQuarterlyInfo = GetNextQuarterlyInfoInPlan();

            if (currentQuarterlyInfo != null)
            {
                if (!await _performanceDataService.Exists(async (set) =>
                {
                    var result = set.Any(pd => pd.Time == currentQuarterlyInfo.Time);
                    return await Task.FromResult(result).ConfigureAwait(false);
                }).ConfigureAwait(false))
                {
                    var time = currentQuarterlyInfo.Time;
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
                            ChargeLeft = await _batteryContainer.GetStateOfChargeInWatts(),
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
            }
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

        /// <summary>
        /// Periodic background routine.
        /// </summary>
        public async Task Process(CancellationToken cancellationToken)
        {
            if (_dayAheadMarketService == null || !_dayAheadMarketService.IsInitialized())
                return;

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!await RefreshQuarterlyPrices().ConfigureAwait(false))
                    return;

                // Enrich for UI + SOC simulation.
                await _consumptionMonitorService.EstimateConsumptionInWattsPerQuarter(_quarterlyInfos).ConfigureAwait(false);
                await _solarService.GetExpectedSolarPower(_quarterlyInfos).ConfigureAwait(false);

                // 1) Plan
                BuildMilpPlan();

                // 2) Economic gating (CycleCost)
                ApplyCycleCostToPlan();

                // 3) SOC feasibility repair (plan-level)
                await EnsureEnergyForPlannedDischargeAsync().ConfigureAwait(false);

                // 4) Write plan to QuarterlyInfos (UI)
                WritePlanIntoQuarterlyInfos();

                // 5) Simulate SOC forward from NOW (UI: ChargeLeft/ChargeNeeded)
                await WriteBackSocSimulationAsync().ConfigureAwait(false);

                // 6) Execute current quarter with runtime guard
                var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

                if (await WeControlTheBatteries().ConfigureAwait(false))
                {
                    var exec = await GetExecutableActionForNowAsync(nowQuarter).ConfigureAwait(false);

                    // If runtime override differs, persist it for UI consistency and re-simulate from now.
                    if (!_planByTime.TryGetValue(nowQuarter, out var plannedNow) ||
                        plannedNow.Mode != exec.Mode ||
                        Math.Abs(plannedNow.PowerW - exec.PowerW) > 0.1)
                    {
                        ApplyRuntimeOverrideToPlan(nowQuarter, exec);
                        WritePlanIntoQuarterlyInfos();
                        await WriteBackSocSimulationAsync().ConfigureAwait(false);
                    }

                    await ExecuteAction(exec).ConfigureAwait(false);
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

                if (DataChanged != null)
                    await DataChanged.Invoke().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Fetches the day-ahead quarter-hour prices.
        /// </summary>
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
        /// Builds a per-quarter optimal plan using MILP and stores it in _planByTime.
        /// IMPORTANT: This only builds a plan; feasibility/economic gating happens afterwards.
        /// </summary>
        private void BuildMilpPlan()
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
                    // Net load in Wh (positive = house drains battery, negative = surplus solar)
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
                ActiveQuarterPenaltyEur: 0.0,
                ForbidSimultaneousChargeDischarge: true,
                TimeLimitMs: 20000
            );

            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt);

            _logger.LogInformation($"MILP plan built: optimal={result.Optimal}, objective={result.ObjectiveEur:F4} EUR");

            var plan = new Dictionary<DateTime, PlanAction>(result.Plan.Count);

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
                        powerW = 0;
                        break;
                }

                plan[p.Start] = new PlanAction { Mode = mode, PowerW = powerW };
            }

            _planByTime = plan;

            // Ensure we have entries for every quarter we might display/simulate.
            foreach (var qi in _quarterlyInfos)
            {
                if (!_planByTime.ContainsKey(qi.Time))
                    _planByTime[qi.Time] = new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
            }
        }

        /// <summary>
        /// Interprets CycleCost as a minimum required spread (EUR/kWh) to allow discharging.
        /// If spread is insufficient, we downgrade planned discharge quarters to ZeroNetHome.
        /// </summary>
        private void ApplyCycleCostToPlan()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0) return;

            double minSpread = _settingsConfig.CycleCost;
            if (minSpread <= 0.0) return;

            // We'll keep a running "best cost basis" for energy that could have been charged earlier.
            // - If we've seen charging in the plan, that becomes our basis (min buy of charging quarters so far).
            // - If no charging seen yet, assume the basis is current quarter buying price (conservative and consistent).
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
        /// Ensures the PLAN is SOC-feasible from "now" forward:
        /// - Simulate SOC from current measured SOC using plan power (W) and net load (Wh)
        /// - If a discharge would be impossible, first try to flip an earlier cheap quarter to charging
        /// - If no candidate exists, downgrade that discharge quarter to ZeroNetHome
        /// This modifies _planByTime (plan-level), not just UI.
        /// </summary>
        private async Task EnsureEnergyForPlannedDischargeAsync()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0) return;

            var now = _timeZoneService.Now.DateFloorQuarter();

            double capWh = _batteryContainer.GetTotalCapacity();
            double socStartWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            // Safety epsilon to avoid floating issues
            const double eps = 0.0001;

            static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

            var future = _quarterlyInfos
                .OrderBy(q => q.Time)
                .Where(q => q.Time >= now)
                .ToList();

            if (future.Count == 0) return;

            // Ensure every future quarter has a plan entry
            foreach (var qi in future)
            {
                if (!_planByTime.ContainsKey(qi.Time))
                    _planByTime[qi.Time] = new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
            }

            const int maxFixes = 500;
            int fixes = 0;

            while (fixes++ < maxFixes)
            {
                double soc = socStartWh;
                DateTime? violationAt = null;

                // 1) Simulate with current plan
                foreach (var qi in future)
                {
                    var act = _planByTime[qi.Time];

                    // Net load (Wh): positive drains SOC
                    double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                    // Planned action energy this quarter (Wh = W * 0.25h)
                    double plannedEnergyWh = Math.Max(0.0, act.PowerW) * 0.25;

                    if (act.Mode == ChargingModes.Modes.Charging)
                    {
                        soc = Math.Min(capWh, soc + plannedEnergyWh);
                    }
                    else if (act.Mode == ChargingModes.Modes.Discharging)
                    {
                        // Need enough SOC (including reserve/hysteresis) to do this planned discharge
                        double required = ReserveWh + SocHysteresisWh + plannedEnergyWh;
                        if (soc < required + eps)
                        {
                            violationAt = qi.Time;
                            break;
                        }

                        soc = Math.Max(0.0, soc - plannedEnergyWh);
                    }

                    // Apply household delta
                    soc = Clamp(soc - netLoadWh, 0.0, capWh);
                }

                if (violationAt == null)
                    return; // plan is feasible

                // 2) Try to repair by flipping an earlier cheap quarter to CHARGING
                // Prefer quarters that are currently NOT discharging and NOT already charging.
                var candidates = future
                    .Where(q => q.Time < violationAt.Value)
                    .Select(q => (qi: q, act: _planByTime[q.Time]))
                    .Where(x => x.act.Mode != ChargingModes.Modes.Discharging)
                    .Where(x => x.act.Mode != ChargingModes.Modes.Charging || x.act.PowerW <= 10)
                    .OrderBy(x => x.qi.BuyingPrice)
                    .ToList();

                if (candidates.Count > 0)
                {
                    var chosen = candidates[0];

                    chosen.act.Mode = ChargingModes.Modes.Charging;
                    chosen.act.PowerW = _batteryContainer.GetChargingCapacityInWattsPerHour();

                    // loop and re-simulate
                    continue;
                }

                // 3) No way to add more charge before violation -> disable that discharge quarter
                var v = _planByTime[violationAt.Value];
                v.Mode = ChargingModes.Modes.ZeroNetHome;
                v.PowerW = 0;

                // loop and re-simulate (may fix later violations too)
            }

            _logger.LogWarning("EnsureEnergyForPlannedDischargeAsync: reached maxFixes; plan may still be infeasible.");
        }

        /// <summary>
        /// Writes plan (Mode + planned power) into QuarterlyInfos for UI/diagnostics.
        /// </summary>
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
        /// SOC simulation for UI:
        /// - Anchor at "now" using measured SOC
        /// - Simulate forward using plan energy and net load
        /// - Does NOT change the plan (no Mode changes here)
        /// </summary>
        private async Task WriteBackSocSimulationAsync()
        {
            if (_quarterlyInfos.Count == 0 || _planByTime.Count == 0) return;

            var nowQuarter = _timeZoneService.Now.DateFloorQuarter();

            double capWh = _batteryContainer.GetTotalCapacity();
            double socNowWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

            // Past: keep whatever is currently there (or set to 0 if you prefer).
            // We'll just ensure it doesn't carry random values.
            foreach (var past in _quarterlyInfos.Where(q => q.Time < nowQuarter))
            {
                // If you want a cleaner chart when ShowAll=true, uncomment:
                // past.SetChargeLeft(0.0);
                // past.SetChargeNeeded(0.0);
            }

            // Forward from NOW (inclusive)
            double soc = socNowWh;

            foreach (var qi in _quarterlyInfos.OrderBy(q => q.Time).Where(q => q.Time >= nowQuarter))
            {
                if (!_planByTime.TryGetValue(qi.Time, out var act))
                    act = new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };

                // Net load (Wh): positive drains SOC
                double netLoadWh = qi.EstimatedConsumptionPerQuarterInWatts - qi.SolarPowerPerQuarterInWatts;

                // Planned action energy this quarter (Wh)
                double plannedEnergyWh = Math.Max(0.0, act.PowerW) * 0.25;

                if (act.Mode == ChargingModes.Modes.Charging)
                {
                    soc = Math.Min(capWh, soc + plannedEnergyWh);
                }
                else if (act.Mode == ChargingModes.Modes.Discharging)
                {
                    // UI simulation should never go negative; feasibility is enforced earlier.
                    soc = Math.Max(0.0, soc - plannedEnergyWh);
                }

                soc = Clamp(soc - netLoadWh, 0.0, capWh);

                qi.SetChargeLeft(soc);

                // For now: show "target" as current simulated SOC.
                // If later the solver outputs a target trajectory, write that here instead.
                qi.SetChargeNeeded(soc);
            }
        }

        /// <summary>
        /// Returns a safe action to execute NOW, using measured SOC.
        /// If planned discharge is infeasible, override to ZeroNetHome (or charge when buy price is negative).
        /// </summary>
        private async Task<PlanAction> GetExecutableActionForNowAsync(DateTime nowQuarter)
        {
            if (!_planByTime.TryGetValue(nowQuarter, out var planned))
                return new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };

            if (planned.Mode != ChargingModes.Modes.Discharging || planned.PowerW <= 10)
                return planned;

            double socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);

            // Required energy for this quarter (Wh)
            double requiredWh = planned.PowerW * 0.25;

            if (socWh <= ReserveWh + SocHysteresisWh + requiredWh)
            {
                var qi = _quarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);

                // Optional: when buy is negative, prefer charging instead of NZH
                if (qi != null && qi.BuyingPrice < 0.0)
                {
                    return new PlanAction
                    {
                        Mode = ChargingModes.Modes.Charging,
                        PowerW = _batteryContainer.GetChargingCapacityInWattsPerHour()
                    };
                }

                return new PlanAction { Mode = ChargingModes.Modes.ZeroNetHome, PowerW = 0 };
            }

            return planned;
        }

        /// <summary>
        /// Writes a runtime override back into the plan so UI stays consistent.
        /// </summary>
        private void ApplyRuntimeOverrideToPlan(DateTime time, PlanAction action)
        {
            _planByTime[time] = action;
        }

        /// <summary>
        /// Executes the action for the current quarter.
        /// NOTE: runtime safety is already handled in GetExecutableActionForNowAsync.
        /// </summary>
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

        /// <summary>
        /// Manual override execution.
        /// </summary>
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


        /// <summary>
        /// Checks who has control. Stores a new record if the controller changes.
        /// </summary>
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

            var last = await _sessyWebControlDataService.Get(async (set) =>
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

        private bool _isDisposed;

        public override void Dispose()
        {
            if (_isDisposed) return;

            _settingsConfigSubscription?.Dispose();
            _sessyBatteryConfigSubscription?.Dispose();

            _quarterlyInfos.Clear();
            _planByTime.Clear();

            _scope.Dispose();

            base.Dispose();
            _isDisposed = true;
        }

        public async Task<double> getBatteryPercentage()
        {
            return await _batteryContainer.GetBatterPercentage().ConfigureAwait(false);
        }
    }
}