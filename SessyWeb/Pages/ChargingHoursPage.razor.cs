using Microsoft.AspNetCore.Components;
using Radzen;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Helpers;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class ChargingHoursPage : PageBase
    {
        [Inject] public TooltipService? tooltipService { get; set; }
        [Inject] public QuarterlyMeasurementDataService? _measurementDataService { get; set; }
        [Inject] public InverterMeasurementDataService? _inverterMeasurementDataService { get; set; }
        [Inject] public SolarService? _solarService { get; set; }
        [Inject] public TimeZoneService? _timeZoneService { get; set; }
        [Inject] public BatteryContainer? _batteryContainer { get; set; }
        [Inject] FinancialResultsService? _finacialResultsService { get; set; }
        [Inject] InverterCurtailmentService? _inverterCurtailmentService { get; set; }
        [Inject] SolarInverterManager? _solarInverterManager { get; set; }

        public List<QuarterlyInfoView>? QuarterlyInfos { get; set; } = new();

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

        private CancellationTokenSource _cts = new();

        private string GraphStyle { get; set; } = "min-width: 250px; visibility: hidden;";

        // Kept name to minimize razor changes; it now means "next planned action".
        private QuarterlyInfo? NextQuarterlyInfoInSession { get; set; }

        private bool _showAll = false;

        private bool ShowAll
        {
            get => _showAll;
            set
            {
                _showAll = value;

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

                        currentThrottlePercentage = _inverterCurtailmentService?.CurrentThrottleW >= double.MaxValue
                            ? 100.0
                            : Math.Round(_inverterCurtailmentService?.CurrentThrottleW / _solarInverterManager?.TotalCapacity ?? 1.0 * 100.0, 1);

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
            await InvokeAsync(async () =>
            {
                IsBusy = true;

                try
                {
                    var now = _timeZoneService!.Now;
                    var today = now.Date;
                    var yesterday = today.AddDays(-1);
                    var tomorrow = today.AddDays(1);

                    await GetQuarterlyInfos();

                    TotalSolarPowerExpectedToday = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(today);
                    TotalSolarPowerExpectedTomorrow = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(tomorrow);

                    // Yesterday's realized solar from QuarterlyMeasurements.
                    TotalSolarPowerYesterday = await GetRealizedSolarForDate(yesterday).ConfigureAwait(false);

                    // Yesterday revenue: realized profit from Performance table (historical).
                    TotalRevenueYesterday = await GetRealizedRevenueForDate(yesterday).ConfigureAwait(false);

                    // Today revenue: expected/planned profit from the current plan (QuarterlyInfos).
                    TotalRevenueToday = await GetRealizedRevenueForDate(today).ConfigureAwait(false); //GetPlannedRevenueForDate(today);

                    BatteryPercentage = await _batteriesService!.getBatteryPercentage().ConfigureAwait(false);
                    BatteryMode = await _batteriesService.GetBatteryMode().ConfigureAwait(false);
                }
                finally
                {
                    IsBusy = false;
                }

                await InvokeAsync(StateHasChanged);
            });
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

            if (measurements.Any(m => m.SolarProductionKWh > 0))
                return measurements.Sum(m => m.SolarProductionKWh);

            // Fallback for dates not yet in QuarterlyMeasurements:
            // use InverterMeasurements directly.
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

            var listFromBatteryService = _batteriesService?.GetQuarterlyInfos() ?? new List<QuarterlyInfo>();

            double averageSellingPrice = listFromBatteryService.Count > 0 ? listFromBatteryService.Average(qi => qi.SellingPrice) : 0.0;
            double averageBuyingPrice = listFromBatteryService.Count > 0 ? listFromBatteryService.Average(qi => qi.BuyingPrice) : 0.0;

            var views = new List<QuarterlyInfoView>();

            if (ShowAll)
            {
                // Historical measurements for the window.
                var measurements = await _measurementDataService!.GetList(async set =>
                {
                    var result = set
                        .Where(m => m.Time >= from && m.Time < to)
                        .ToList();

                    return await Task.FromResult(result);
                }).ConfigureAwait(false);

                // Historical measurement times — these take priority over planning data.
                var measuredTimes = new HashSet<DateTime>(measurements.Select(m => m.Time));

                // Plan items — only include quarters not covered by actual measurements.
                var planItems = listFromBatteryService
                    .Where(q => q.Time >= from && q.Time < to && !measuredTimes.Contains(q.Time))
                    .OrderBy(q => q.Time)
                    .ToList();

                foreach (var qi in planItems)
                    views.Add(await FillQuarterlyInfoView(qi, averageBuyingPrice, averageSellingPrice).ConfigureAwait(false));

                foreach (var m in measurements)
                    views.Add(FillQuarterlyInfoView(m));
            }
            else
            {
                // Non-showall: show from now quarter onward.
                // For the current quarter, use the actual measurement if available
                // (it reflects the real executed mode, not just the plan).
                var nowQ = _timeZoneService!.Now.DateFloorQuarter();

                var currentMeasurement = await _measurementDataService!.Get(async set =>
                    await Task.FromResult(set.FirstOrDefault(m => m.Time == nowQ)))
                    .ConfigureAwait(false);

                var planItems = listFromBatteryService
                    .Where(q => q.Time >= nowQ && q.Time < nowQ.Date.AddDays(2))
                    .OrderBy(q => q.Time)
                    .ToList();

                foreach (var qi in planItems)
                {
                    if (qi.Time == nowQ && currentMeasurement != null)
                        // Current quarter: show actual measurement (real mode) instead of plan.
                        views.Add(FillQuarterlyInfoView(currentMeasurement));
                    else
                        views.Add(await FillQuarterlyInfoView(qi, averageBuyingPrice, averageSellingPrice).ConfigureAwait(false));
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

        public async Task<QuarterlyInfoView> FillQuarterlyInfoView(QuarterlyInfo quarterlyInfo, double averageBuyingPrice, double averageSellingPrice)
        {
            var totalCapacityWh = _batteryContainer!.GetTotalCapacity();
            var chargeLeftWh = quarterlyInfo.ChargeLeftWh;
            var chargeNeededWh = quarterlyInfo.ChargeNeededWh;
            var chargeLeftPct = totalCapacityWh > 0 ? (chargeLeftWh / totalCapacityWh) * 100.0 : 0.0;

            // For the current quarter: show actual battery state instead of MILP plan.
            // The MILP plan may differ from what the battery is actually doing because
            // GetExecutableActionForNowAsync applies real-time corrections (e.g. export
            // threshold check that overrides Discharging → ZeroNetHome).
            bool isCurrentQuarter = quarterlyInfo.Time == _timeZoneService!.Now.DateFloorQuarter();

            string displayState;
            double chargePowerW;
            double dischargePowerW;

            if (isCurrentQuarter && _batteriesService != null)
            {
                // Read actual mode and power from BatteriesService.
                var actualPowerW = await _batteryContainer.GetTotalPowerInWatts().ConfigureAwait(false);
                var actualMode = await _batteriesService.GetBatteryMode().ConfigureAwait(false);

                displayState = actualMode;
                chargePowerW = actualPowerW < 0 ? Math.Abs(actualPowerW) : 0.0;
                dischargePowerW = actualPowerW > 0 ? actualPowerW : 0.0;
            }
            else
            {
                displayState = quarterlyInfo.GetDisplayMode() ?? string.Empty;
                chargePowerW = quarterlyInfo.PlannedChargePowerW;
                dischargePowerW = quarterlyInfo.PlannedDischargePowerW;
            }

            return await Task.FromResult(new QuarterlyInfoView
            {
                Time = quarterlyInfo.Time,
                SessionId = null,

                IsPriceExpected = quarterlyInfo.IsPriceExpected,
                BuyingPrice = quarterlyInfo.BuyingPrice,
                SellingPrice = quarterlyInfo.SellingPrice,
                MarketPrice = quarterlyInfo.MarketPrice,

                Profit = quarterlyInfo.Profit,

                SmoothedBuyingPrice = quarterlyInfo.SmoothedBuyingPrice,
                SmoothedSellingPrice = quarterlyInfo.SmoothedSellingPrice,

                VisualizeInChart = quarterlyInfo.VisualizeInChart(),

                ChargeLeft = chargeLeftWh,
                ChargeNeeded = chargeNeededWh,

                EstimatedConsumptionPerQuarterHour = quarterlyInfo.EstimatedConsumptionPerQuarterInWatts,
                SolarPowerPerQuarterHour = quarterlyInfo.SolarPowerPerQuarterHour,
                SolarGlobalRadiation = quarterlyInfo.SolarGlobalRadiation,

                ChargeLeftPercentage = chargeLeftPct,
                DisplayState = displayState,
                Price = quarterlyInfo.Price,

                ChargeNeededPercentage = totalCapacityWh > 0 ? (chargeNeededWh / totalCapacityWh) * 100.0 : 0.0,
                SmoothedSolarPower = quarterlyInfo.SmoothedSolarPower,

                AverageBuyingPrice = averageBuyingPrice,
                AverageSellingPrice = averageSellingPrice,

                SessionCost = null,
                DeltaLowestPrice = quarterlyInfo.DeltaLowestPrice,

                ChargePowerW = chargePowerW,
                DischargePowerW = dischargePowerW
            });
        }

        public QuarterlyInfoView FillQuarterlyInfoView(QuarterlyMeasurement measurement)
        {
            double totalCapacityWh = _batteryContainer!.GetTotalCapacity();
            double chargeLeftPct = totalCapacityWh > 0
                ? measurement.BatteryStateOfChargeWh / totalCapacityWh * 100.0
                : 0.0;

            string displayState = measurement.BatteryMode switch
            {
                SessyData.Model.BatteryMode.Charging => "Charging",
                SessyData.Model.BatteryMode.Discharging => "Discharging",
                SessyData.Model.BatteryMode.ZeroNetHome => "ZeroNetHome",
                _ => "Disabled"
            };

            return new QuarterlyInfoView
            {
                Time = measurement.Time,
                SessionId = null,

                IsPriceExpected = false,
                BuyingPrice = measurement.BuyingPriceEur,
                SellingPrice = measurement.SellingPriceEur,
                MarketPrice = 0.0,

                Profit = 0.0,

                SmoothedBuyingPrice = measurement.BuyingPriceEur,
                SmoothedSellingPrice = measurement.SellingPriceEur,

                VisualizeInChart = 1.0,

                ChargeLeft = measurement.BatteryStateOfChargeWh,
                ChargeNeeded = 0.0,

                EstimatedConsumptionPerQuarterHour = 0.0,
                SolarPowerPerQuarterHour = measurement.SolarProductionKWh,
                SolarGlobalRadiation = measurement.GlobalRadiation,

                ChargeLeftPercentage = chargeLeftPct,
                DisplayState = displayState,
                Price = measurement.BatteryMode == SessyData.Model.BatteryMode.Charging
                    ? measurement.BuyingPriceEur
                    : measurement.SellingPriceEur,

                ChargeNeededPercentage = 0.0,
                SmoothedSolarPower = measurement.SolarProductionKWh,

                SessionCost = null,
                DeltaLowestPrice = 0.0,

                // Actual battery power from measurement.
                // BatteryPowerWatts: negative = charging, positive = discharging.
                ChargePowerW = measurement.BatteryPowerWatts < 0 ? Math.Abs(measurement.BatteryPowerWatts) : 0.0,
                DischargePowerW = measurement.BatteryPowerWatts > 0 ? measurement.BatteryPowerWatts : 0.0
            };
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

            // 13 pixels per data row (3)
            var width = (QuarterlyInfos?.Count ?? 0) * 3 * 13;

            // Clamp the width so Radzen doesn't explode on very large datasets
            if (width < 600) width = 600;
            if (width > 8000) width = 8000;

            GraphStyle = $"min-height: {height}px; width: {width}px; visibility: initial;";
        }

        private bool _isDisposed = false;
        private double? currentThrottlePercentage;

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