using Microsoft.AspNetCore.Components;
using Radzen;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Managers;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.StateMachine;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Helpers;
using static SessyWeb.Components.DateChooserComponent;
using SessyWeb.Components;

namespace SessyWeb.Pages
{
    public partial class ChargingHoursPage : PageBase
    {
        [Inject] public TooltipService? tooltipService { get; set; }
        [Inject] public QuarterlyMeasurementDataService? _measurementDataService { get; set; }
        [Inject] public InverterMeasurementDataService? _inverterMeasurementDataService { get; set; }
        [Inject] public PlannedQuarterDataService? _plannedQuarterDataService { get; set; }
        [Inject] public SolarService? _solarService { get; set; }
        [Inject] public TimeZoneService? _timeZoneService { get; set; }
        [Inject] public ICalculationService? _calculationService { get; set; }
        [Inject] public BatteryContainer? _batteryContainer { get; set; }
        [Inject] FinancialResultsService? _finacialResultsService { get; set; }
        [Inject] private HardwareStatusService? _hardwareStatusService { get; set; }
        [Inject] private EnergySystemStateMachine? _stateMachine { get; set; }
        [Inject] private IMilpService? _milpService { get; set; }
        [Inject] private SettingsService? _settingsService { get; set; }

        private double SocDeviationPct { get; set; } = 0.0;
        private string LastPlanReason { get; set; } = string.Empty;
        private DateTime? LastPlanSavedAt { get; set; }
        [Inject] SolarInverterManager? _solarInverterManager { get; set; }

        public List<QuarterlyInfoView>? QuarterlyInfos { get; set; } = new();

        // Plan history overlay — bound via @ref, mirrors ShowAll automatically (see GetQuarterlyInfos).
        private ChargingHoursChartComponent? _chartComponent;

        private async Task OnPlanSelected(object value)
        {
            if (_chartComponent == null) return;
            await _chartComponent.OnPlanSelected((Guid?)value).ConfigureAwait(false);
            await InvokeAsync(StateHasChanged);
        }

        // Measurement cache — avoids re-querying the DB every second on DataChanged.
        // Invalidated when the date window or ShowAll flag changes.
        private List<QuarterlyMeasurement>? _cachedMeasurements;
        private Dictionary<DateTime, double> _cachedSolarByQuarter = new();
        private DateTime _cachedSolarFrom;
        private DateTime _cachedSolarTo;
        private DateTime _cachedFrom;
        private DateTime _cachedTo;
        private bool _showAllWhenCached;
        private DateTime _cacheTimestamp;

        public double TotalSolarPowerExpectedToday { get; private set; }
        public double TotalSolarPowerExpectedTomorrow { get; private set; }
        public double TotalSolarPowerYesterday { get; private set; }

        public string TotalSolarPowerExpectedTodayVisual => TotalSolarPowerExpectedToday.ToString("0.#");
        public string TotalSolarPowerExpectedTomorrowVisual => TotalSolarPowerExpectedTomorrow.ToString("0.#");
        public string TotalSolarPowerYesterdayVisual => TotalSolarPowerYesterday.ToString("0.#");

        public decimal TotalRevenueToday { get; set; }
        public decimal TotalRevenueYesterday { get; set; }

        public string TotalRevenueExpectedTodayVisual => TotalRevenueToday.ToString("0.00");
        public string TotalRevenueExpectedYesterdayVisual => TotalRevenueYesterday.ToString("0.00");

        public double BatteryPercentage { get; set; }
        public string BatteryPercentageVisual => BatteryPercentage.ToString("##0.0%");

        public string? BatteryMode { get; set; }

        public string OptimizationStrategyName => _settingsService?.Current.Strategy switch
        {
            SessyData.Model.OptimizationStrategy.SelfConsumption => "Self consumption",
            SessyData.Model.OptimizationStrategy.Balanced => "Balanced",
            SessyData.Model.OptimizationStrategy.BatterySaving => "Battery saving",
            _ => "Profit maximization"
        };

        private CancellationTokenSource _cts = new();

        private string GraphStyle { get; set; } = "min-width: 250px; visibility: hidden;";

        // Kept name to minimize razor changes; it now means "next planned action".
        private QuarterlyInfo? NextQuarterlyInfoInSession { get; set; }

        private bool _showAll = false;

        public bool ShowAll
        {
            get => _showAll;
            set
            {
                _showAll = value;
                _cachedMeasurements = null; // Invalidate cache on ShowAll toggle.

                Task task = GetQuarterlyInfos();

                Task.WhenAll(task);

                HandleScreenHeight();
            }
        }

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();

            try
            {
                _ = UpdateLoop();
            }
            catch
            {
                // Keep it silent.
            }
        }

        private async Task _batteriesService_DataChanged()
        {
            await UpdateLoop();
        }

        /// <summary>
        /// List with battery statuses.
        /// </summary>
        public List<BatteryWithStatus>? BatteryWithStatusList { get; set; }

        /// <summary>
        /// Class that holds battery status.
        /// </summary>
        public class BatteryWithStatus
        {
            public Battery Battery { get; set; } = default!;
            public string StatusColor => PowerStatus!.Sessy!.SystemStateColor;
            public string StatusTitle => PowerStatus!.Sessy!.SystemStateTitle;
            public PowerStatus? PowerStatus { get; set; }
        }

        /// <summary>
        /// Get the statuses of the batteries in a loop.
        /// </summary>
        private async Task UpdateLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var newStatuses = new List<BatteryWithStatus>();

                    foreach (var battery in batteryContainer!.Batteries!)
                    {
                        var powerStatus = await battery.GetPowerStatus().ConfigureAwait(false);

                        currentThrottlePercentage = _hardwareStatusService?.ThrottlePct ?? 100.0;

                        newStatuses.Add(new BatteryWithStatus
                        {
                            Battery = battery,
                            PowerStatus = powerStatus
                        });
                    }

                    BatteryWithStatusList = newStatuses;

                    // Solver-based: next planned action
                    NextQuarterlyInfoInSession = _batteriesService?.GetNextQuarterlyInfoInPlan();

                    await InvokeAsync(StateHasChanged);
                }
                catch
                {
                    // swallow, keep loop alive
                }

                try
                {
                    await Task.Delay(5000, _cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // ignore cancellation / delay errors
                }
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                _batteriesService!.DataChanged += BatteriesServiceDataChanged;
                _batteriesService!.OnHeartBeat += HeartBeat;

                await InvokeAsync(async () =>
                {
                    await BatteriesServiceDataChanged();
                    HandleScreenHeight();
                    StateHasChanged();
                });
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        /// <summary>
        /// Handle the height of the screen.
        /// </summary>
        public override void ScreenInfoChanged(ScreenInfo screenInfo)
        {
            HandleScreenHeight();
        }

        private void HandleScreenHeight()
        {
            var height = ScreenInfo?.Height ?? 0;
            var width = ScreenInfo?.Width ?? 0;

            // If screen size is not known yet, use a sane fallback
            if (height <= 0) height = 900;
            if (width <= 0) width = 1400;

            HandleResize(height - 300, width);
        }

        private void HandleResize(int height, int width)
        {
            ChangeChartStyle(height);
        }

        /// <summary>
        /// The heartbeat is called.
        /// </summary>
        private async Task HeartBeat()
        {
            await InvokeAsync(async () =>
            {
                IsBeating = true;
                await InvokeAsync(StateHasChanged);

                await Task.Delay(3000).ContinueWith(async _ =>
                {
                    IsBeating = false;
                    await InvokeAsync(StateHasChanged);
                });
            });
        }

        private bool IsBeating = false;

        /// <summary>
        /// The data changed event is fired. Refresh the data.
        /// </summary>
        private async Task BatteriesServiceDataChanged()
        {
            IsBusy = true;
            await InvokeAsync(StateHasChanged); // show spinner immediately

            try
            {
                var now = _timeZoneService!.Now;
                var today = now.Date;
                var yesterday = today.AddDays(-1);
                var tomorrow = today.AddDays(1);

                // Heavy work runs outside InvokeAsync so the Blazor circuit thread
                // is not blocked — DB queries, price calculations, plan building.
                await GetQuarterlyInfos();

                var realizedToday = await GetRealizedSolarForDate(today).ConfigureAwait(false);
                var forecastRemainingToday = _solarService == null ? 0.0 : _solarService.GetRemainingForecastToday(today, _timeZoneService!.Now);
                var totalSolarToday = realizedToday + forecastRemainingToday;
                var totalSolarTomorrow = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(tomorrow);
                var totalSolarYesterday = await GetRealizedSolarForDate(yesterday).ConfigureAwait(false);
                var revenueYesterday = await GetRealizedRevenueForDate(yesterday).ConfigureAwait(false);
                var revenueToday = await GetRealizedRevenueForDate(today).ConfigureAwait(false);
                var batteryPct = await _batteriesService!.getBatteryPercentage().ConfigureAwait(false);
                var batteryMode = await _batteriesService.GetBatteryMode().ConfigureAwait(false);
                var planStats = await _milpService!.GetPlanStatisticsAsync(_timeZoneService!.Now, _hardwareStatusService?.CurrentSocWh ?? 0.0).ConfigureAwait(false);

                // Apply results and trigger a single UI update.
                await InvokeAsync(() =>
                {
                    TotalSolarPowerExpectedToday = totalSolarToday;
                    TotalSolarPowerExpectedTomorrow = totalSolarTomorrow;
                    TotalSolarPowerYesterday = totalSolarYesterday;
                    TotalRevenueYesterday = revenueYesterday;
                    TotalRevenueToday = revenueToday;
                    BatteryPercentage = batteryPct;
                    BatteryMode = batteryMode;
                    SocDeviationPct = planStats.SocDeviationPct;
                    var latestPlan = planStats.RecentHistory.FirstOrDefault();
                    LastPlanReason = latestPlan?.Reason ?? string.Empty;
                    LastPlanSavedAt = latestPlan?.SavedAt;
                    HandleScreenHeight();
                    StateHasChanged();
                });
            }
            finally
            {
                IsBusy = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        private async Task<double> GetRealizedSolarForDate(DateTime date)
        {
            var start = date.Date;
            var end = start.AddDays(1);

            // QuarterlyMeasurements is the single source of truth for solar after
            // the MigrateSolarInverterDataToInverterMeasurements migration.
            // For dates before InverterMeasurements existed, QM is populated via
            // the same migration from the historical SolarInverterData table.
            var measurements = await _measurementDataService!.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= start && m.Time < end)
                    .ToList();
                return await Task.FromResult(result);
            });

            // Solar production comes from InverterMeasurements (source of truth).
            if (_inverterMeasurementDataService != null)
            {
                var inverterMeasurements = await _inverterMeasurementDataService.GetList(async set =>
                {
                    var result = set
                        .Where(m => m.Time >= start && m.Time < end)
                        .ToList();
                    return await Task.FromResult(result);
                });

                if (inverterMeasurements.Any())
                    return inverterMeasurements.Sum(m => m.SolarProductionKWh);
            }

            return 0.0;
        }

        private async Task<decimal> GetRealizedRevenueForDate(DateTime date)
        {
            if (_finacialResultsService == null) return 0M;

            return await _finacialResultsService.GetFinancialResultForDate(date);
        }

        /// <summary>
        /// Retrieve all the quarterlyInfo objects within the selected date time and period.
        /// </summary>
        private async Task GetQuarterlyInfos()
        {
            // Determine base date safely
            DateTime baseDate;
            if (ShowAll)
                baseDate = DateSelectionChosen?.Start ?? _timeZoneService!.Now;
            else
                baseDate = _timeZoneService!.Now;

            var from = baseDate.Date.AddDays(-1);
            var to = baseDate.Date.AddDays(2); // yesterday..tomorrow (3-day window)

            if (_chartComponent != null)
                await _chartComponent.SetPlanHistoryWindowAsync(ShowAll, from, to).ConfigureAwait(false);

            var listFromBatteryService = _batteriesService?.GetQuarterlyInfos() ?? new List<QuarterlyInfo>();

            // Apply solar and consumption smoothing on QuarterlyInfo before building views.
            QuarterlyInfo.ApplySolarSmoothing(listFromBatteryService, windowSize: 12);
            QuarterlyInfo.ApplyConsumptionSmoothing(listFromBatteryService, windowSize: 12);

            double totalCapacityWh = _batteryContainer?.GetTotalCapacity() ?? 0.0;
            double averageSellingPrice = listFromBatteryService.Count > 0 ? listFromBatteryService.Average(qi => qi.SellingPrice) : 0.0;
            double averageBuyingPrice = listFromBatteryService.Count > 0 ? listFromBatteryService.Average(qi => qi.BuyingPrice) : 0.0;

            var views = new List<QuarterlyInfoView>();

            if (ShowAll)
            {
                // Re-query when the window/ShowAll changed, or when the visible window
                // includes the current quarter and the cache predates it (new data arrived).
                var nowQuarter = _timeZoneService!.Now.DateFloorQuarter();
                bool windowIncludesNow = nowQuarter >= from && nowQuarter < to;
                bool windowChanged = _cachedMeasurements == null
                    || _showAllWhenCached != ShowAll
                    || _cachedFrom != from
                    || _cachedTo != to
                    || (windowIncludesNow && _cacheTimestamp < nowQuarter);

                if (windowChanged)
                {
                    _cachedMeasurements = await _measurementDataService!.GetList(async set =>
                    {
                        var result = set
                            .Where(m => m.Time >= from && m.Time < to)
                            .ToList();

                        return await Task.FromResult(result);
                    }).ConfigureAwait(false);

                    _cachedFrom = from;
                    _cachedTo = to;
                    _showAllWhenCached = ShowAll;
                    _cacheTimestamp = nowQuarter;
                }

                var measurements = _cachedMeasurements!;

                // Fetch solar from InverterMeasurements — refresh whenever measurements were refreshed.
                if (_inverterMeasurementDataService != null && windowChanged)
                {
                    var inverterData = await _inverterMeasurementDataService.GetList(async set =>
                        await Task.FromResult(set.Where(m => m.Time >= from && m.Time < to).ToList()))
                        .ConfigureAwait(false);
                    _cachedSolarByQuarter = inverterData
                        .GroupBy(m => m.Time)
                        .ToDictionary(g => g.Key, g => g.Sum(m => m.SolarProductionKWh));
                }
                var solarByQuarter = _cachedSolarByQuarter;

                // Historical measurement times — these take priority over planning data.
                var measuredTimes = new HashSet<DateTime>(measurements.Select(m => m.Time));

                // Plan items — only include quarters not covered by actual measurements.
                var planItems = listFromBatteryService
                    .Where(q => q.Time >= from && q.Time < to && !measuredTimes.Contains(q.Time))
                    .OrderBy(q => q.Time)
                    .ToList();

                // Planned state from PlannedQuarter — used for both plan-only and measured quarters.
                var plannedByQuarter = await GetPlannedByQuarterAsync(from, to).ConfigureAwait(false);

                foreach (var qi in planItems)
                {
                    plannedByQuarter.TryGetValue(qi.Time, out var pq);
                    views.Add(new QuarterlyInfoView(qi, totalCapacityWh, pq, averageBuyingPrice, averageSellingPrice));
                }

                // Plan solar by quarter — fallback when a measurement has no inverter data yet
                // (InverterMeasurements are written slightly after QuarterlyMeasurements).
                var planSolarByQuarter = listFromBatteryService
                    .GroupBy(q => q.Time)
                    .ToDictionary(g => g.Key, g => g.First().SolarPowerPerQuarterHour);

                // Batch-calculate buying/selling prices from EPEXPrices + Taxes.
                var measurementPrices = _calculationService != null
                    ? await _calculationService.CalculateEnergyPricesBatchAsync(measurements.Select(m => m.Time))
                    : new Dictionary<DateTime, EnergyPrice>();

                foreach (var m in measurements)
                {
                    if (measurementPrices.TryGetValue(m.Time, out var p))
                    {
                        m.BuyingPriceEur = p.Buying;
                        m.SellingPriceEur = p.Selling;
                    }

                    double solarKWh = solarByQuarter != null && solarByQuarter.TryGetValue(m.Time, out var s) ? s : 0.0;
                    double planSolarKWh = planSolarByQuarter.TryGetValue(m.Time, out var ps) ? ps : 0.0;

                    var realizedQi = new QuarterlyInfo(m, solarKWh, planSolarKWh,
                        _settingsService!, _solarInverterManager!, _timeZoneService!);
                    plannedByQuarter.TryGetValue(m.Time, out var pq);
                    var view = new QuarterlyInfoView(realizedQi, totalCapacityWh, pq, averageBuyingPrice, averageSellingPrice);
                    views.Add(view);
                }
            }
            else
            {
                // Non-showall: show from now quarter onward.
                var nowQ = _timeZoneService!.Now.DateFloorQuarter();

                var currentMeasurement = await _measurementDataService!.Get(async set =>
                    await Task.FromResult(set.FirstOrDefault(m => m.Time == nowQ)))
                    .ConfigureAwait(false);

                // Fetch solar for today from InverterMeasurements — use cache when possible.
                if (_inverterMeasurementDataService != null &&
                    (_cachedSolarFrom != nowQ.Date || _cachedSolarTo != nowQ.AddMinutes(15)))
                {
                    var inverterData = await _inverterMeasurementDataService.GetList(async set =>
                        await Task.FromResult(set.Where(m => m.Time >= nowQ.Date && m.Time <= nowQ).ToList()))
                        .ConfigureAwait(false);
                    _cachedSolarByQuarter = inverterData
                        .GroupBy(m => m.Time)
                        .ToDictionary(g => g.Key, g => g.Sum(m => m.SolarProductionKWh));
                    _cachedSolarFrom = nowQ.Date;
                    _cachedSolarTo = nowQ.AddMinutes(15);
                }
                var nowSolarByQuarter = _cachedSolarByQuarter;

                var planItems = listFromBatteryService
                    .Where(q => q.Time >= nowQ && q.Time < nowQ.Date.AddDays(2))
                    .OrderBy(q => q.Time)
                    .ToList();

                var nowPlanSolarByQuarter = planItems
                    .ToDictionary(q => q.Time, q => q.SolarPowerPerQuarterHour);

                // Planned state from PlannedQuarter — without this, the tooltip's "Planned" field
                // falls back to qi.GetDisplayMode(), which shows "?" whenever qi.Mode was never
                // set on the in-memory QuarterlyInfo, even though the real plan exists in the DB.
                var nowPlannedByQuarter = await GetPlannedByQuarterAsync(nowQ, nowQ.Date.AddDays(2)).ConfigureAwait(false);

                foreach (var qi in planItems)
                {
                    nowPlannedByQuarter.TryGetValue(qi.Time, out var pq);

                    if (qi.Time == nowQ && currentMeasurement != null)
                    {
                        if (_calculationService != null)
                        {
                            var p = await _calculationService.CalculateEnergyPricesBatchAsync(
                                new[] { currentMeasurement.Time });
                            if (p.TryGetValue(currentMeasurement.Time, out var prices))
                            {
                                currentMeasurement.BuyingPriceEur = prices.Buying;
                                currentMeasurement.SellingPriceEur = prices.Selling;
                            }
                        }

                        double solarKWh = nowSolarByQuarter != null && nowSolarByQuarter.TryGetValue(currentMeasurement.Time, out var s) ? s : 0.0;
                        double planSolarKWh = nowPlanSolarByQuarter.TryGetValue(currentMeasurement.Time, out var ps) ? ps : qi.SolarPowerPerQuarterHour;

                        var realizedQi = new QuarterlyInfo(currentMeasurement, solarKWh, planSolarKWh,
                            _settingsService!, _solarInverterManager!, _timeZoneService!);
                        views.Add(new QuarterlyInfoView(realizedQi, totalCapacityWh, pq, averageBuyingPrice, averageSellingPrice));
                    }
                    else
                    {
                        bool isNow = qi.Time == nowQ;
                        if (nowSolarByQuarter != null && nowSolarByQuarter.TryGetValue(qi.Time, out var measuredSolar) && measuredSolar > 0)
                            qi.SolarPowerPerQuarterHour = measuredSolar;
                        string? actualDisplay = isNow ? _hardwareStatusService!.ActualBatteryStrategy : null;
                        double? actualPowerW = isNow ? _hardwareStatusService!.ActualBatteryPowerW : null;
                        views.Add(new QuarterlyInfoView(qi, totalCapacityWh, pq, averageBuyingPrice, averageSellingPrice,
                            actualDisplay, actualPowerW, currentThrottlePercentage));
                    }
                }
            }

            // Remove duplicate timestamps (Performance + Plan can overlap)
            QuarterlyInfos = views
                .GroupBy(v => v.Time)
                .Select(g => g.First()) // keep first; or prefer Performance over Plan if you want
                .OrderBy(v => v.Time)
                .ToList();

            HandleScreenHeight();
            await InvokeAsync(StateHasChanged);
        }

        /// <summary>
        /// Loads planned quarters from the database for the given window, keyed by time.
        /// Returns empty dictionary when the service is unavailable.
        /// </summary>
        /// <summary>
        /// Applies a centered moving average to EstimatedConsumptionPerQuarterHour
        /// and stores the result in SmoothedConsumptionPerQuarterHour for display.
        /// </summary>

        private async Task<Dictionary<DateTime, PlannedQuarter>> GetPlannedByQuarterAsync(DateTime from, DateTime to)
        {
            if (_plannedQuarterDataService == null)
                return new Dictionary<DateTime, PlannedQuarter>();

            var planned = await _plannedQuarterDataService.GetList(async set =>
                await Task.FromResult(set.Where(p => p.Time >= from && p.Time < to).ToList()))
                .ConfigureAwait(false);

            return planned
                .GroupBy(p => p.Time)
                .ToDictionary(g => g.Key, g => g.First());
        }

        public async Task SelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;
            await GetQuarterlyInfos();
        }

        public DateArgs? DateSelectionChosen { get; private set; }

        /// <summary>
        /// Change the width of the chart depending on the number of quarterlyInfo objects.
        /// </summary>
        private void ChangeChartStyle(int height)
        {
            // Prevent invalid/negative heights during first render
            if (height < 250) height = 250;

            // ~13 px per bar; 4 column bars per quarter (buy, sell, cost basis, revenue).
            var width = (QuarterlyInfos?.Count ?? 0) * 4 * 13;

            // Clamp the width so Radzen doesn't explode on very large datasets
            if (width < 600) width = 600;
            if (width > 10000) width = 10000;

            GraphStyle = $"min-height: {height}px; width: {width}px; visibility: initial;";
        }

        private bool _isDisposed = false;
        private double currentThrottlePercentage = 100.0;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;

                try
                {
                    _batteriesService!.DataChanged -= BatteriesServiceDataChanged;
                    _batteriesService!.OnHeartBeat -= HeartBeat;
                }
                catch
                {
                    // ignore dispose races
                }

                try
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            base.Dispose();
        }
    }
}