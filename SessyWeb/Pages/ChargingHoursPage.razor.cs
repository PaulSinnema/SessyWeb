using Microsoft.AspNetCore.Components;
using Radzen;
using SessyCommon.Extensions;
using SessyCommon.Services;
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
        [Inject] public PerformanceDataService? _performanceDataService { get; set; }
        [Inject] public SolarService? _solarService { get; set; }
        [Inject] public TimeZoneService? _timeZoneService { get; set; }
        [Inject] public BatteryContainer? _batteryContainer { get; set; }
        [Inject] FinancialResultsService? _finacialResultsService { get; set; }

        public List<QuarterlyInfoView>? QuarterlyInfos { get; set; } = new();

        public double TotalSolarPowerExpectedToday { get; private set; }
        public double TotalSolarPowerExpectedTomorrow { get; private set; }

        public string TotalSolarPowerExpectedTodayVisual => TotalSolarPowerExpectedToday.ToString("0.#");
        public string TotalSolarPowerExpectedTomorrowVisual => TotalSolarPowerExpectedTomorrow.ToString("0.#");

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
                // Plan (future/current) limited to window
                var planItems = listFromBatteryService
                    .Where(q => q.Time >= from && q.Time < to)
                    .OrderBy(q => q.Time)
                    .ToList();

                foreach (var qi in planItems)
                    views.Add(await FillQuarterlyInfoView(qi, averageBuyingPrice, averageSellingPrice).ConfigureAwait(false));

                // Performance (historical) limited to the SAME window
                var perfItems = await _performanceDataService!.GetList(async set =>
                {
                    var result = set
                        .Where(p => p.Time >= from && p.Time < to)
                        .ToList();

                    return await Task.FromResult(result);
                }).ConfigureAwait(false);

                var totalCapacity = _batteryContainer!.GetTotalCapacity();

                foreach (var p in perfItems)
                    views.Add(FillQuarterlyInfoView(p, totalCapacity));
            }
            else
            {
                // Non-showall: just show from now quarter onward, but still clamp to tomorrow to avoid huge lists
                var nowQ = _timeZoneService!.Now.DateFloorQuarter();
                var planItems = listFromBatteryService
                    .Where(q => q.Time >= nowQ && q.Time < nowQ.Date.AddDays(2))
                    .OrderBy(q => q.Time)
                    .ToList();

                foreach (var qi in planItems)
                    views.Add(await FillQuarterlyInfoView(qi, averageBuyingPrice, averageSellingPrice).ConfigureAwait(false));
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
            // No sessions in solver-based approach.
            var totalCapacityWh = _batteryContainer!.GetTotalCapacity();

            // If you renamed ChargeLeft/Needed fields differently, adjust here.
            var chargeLeftWh = quarterlyInfo.ChargeLeftWh;
            var chargeNeededWh = quarterlyInfo.ChargeNeededWh;

            var chargeLeftPct = totalCapacityWh > 0 ? (chargeLeftWh / totalCapacityWh) * 100.0 : 0.0;

            return await Task.FromResult(new QuarterlyInfoView
            {
                Time = quarterlyInfo.Time,
                SessionId = null, // sessions removed

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
                DisplayState = quarterlyInfo.GetDisplayMode() ?? string.Empty,
                Price = quarterlyInfo.Price,

                // Keep existing view fields
                ChargeNeededPercentage = totalCapacityWh > 0 ? (chargeNeededWh / totalCapacityWh) * 100.0 : 0.0,
                SmoothedSolarPower = quarterlyInfo.SmoothedSolarPower,

                AverageBuyingPrice = averageBuyingPrice,
                AverageSellingPrice = averageSellingPrice,

                SessionCost = null, // sessions removed
                DeltaLowestPrice = quarterlyInfo.DeltaLowestPrice
            });
        }

        public QuarterlyInfoView FillQuarterlyInfoView(Performance performance, double totalCapacityWh)
        {
            // Fix the percentage formula (it was inverted in your old code).
            var neededPct = totalCapacityWh > 0 ? (performance.ChargeNeeded / totalCapacityWh) * 100.0 : 0.0;

            return new QuarterlyInfoView
            {
                Time = performance.Time,
                SessionId = null,

                BuyingPrice = performance.BuyingPrice,
                SellingPrice = performance.SellingPrice,
                MarketPrice = performance.MarketPrice,

                Profit = performance.Profit,

                SmoothedBuyingPrice = performance.BuyingPrice,
                SmoothedSellingPrice = performance.SellingPrice,

                VisualizeInChart = performance.VisualizeInChart,

                ChargeLeft = performance.ChargeLeft,
                ChargeNeeded = performance.ChargeNeeded,

                EstimatedConsumptionPerQuarterHour = performance.EstimatedConsumptionPerQuarterHour,
                SolarPowerPerQuarterHour = performance.SolarPowerPerQuarterHour,
                SolarGlobalRadiation = performance.SolarGlobalRadiation,

                ChargeLeftPercentage = performance.ChargeLeftPercentage,
                DisplayState = performance.DisplayState ?? string.Empty,
                Price = performance.Price,

                ChargeNeededPercentage = neededPct,
                SmoothedSolarPower = performance.SmoothedSolarPower,

                SessionCost = null,
                DeltaLowestPrice = 0.0
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
