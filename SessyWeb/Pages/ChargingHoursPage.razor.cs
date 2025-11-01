using Microsoft.AspNetCore.Components;
using Radzen;
using SessyCommon.Configurations;
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
        [Inject]
        public TooltipService? tooltipService { get; set; }
        [Inject]
        public BatteriesService? _batteriesService { get; set; }
        [Inject]
        public PerformanceDataService? _performanceDataService { get; set; }
        [Inject]
        public SolarService? _solarService { get; set; }
        [Inject]
        public TimeZoneService? _timeZoneService { get; set; }

        public List<QuarterlyInfoView>? QuarterlyInfos { get; set; } = new List<QuarterlyInfoView>();

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

        private string RowHeightStyle { get; set; } = "height 20px";
        private string GraphStyle { get; set; } = "min-width: 250px; visibility: hidden;";

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
            catch (Exception)
            {
                // Keep it silent.
            }
        }

        /// <summary>
        /// List with battery statusses.
        /// </summary>
        public List<BatteryWithStatus>? BatteryWithStatusList { get; set; }

        /// <summary>
        /// Class that holds the battery status.
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
            while (true)
            {
                var newStatuses = new List<BatteryWithStatus>();

                foreach (var battery in batteryContainer!.Batteries!)
                {
                    var powerStatus = await battery.GetPowerStatus();

                    newStatuses.Add(new BatteryWithStatus
                    {
                        Battery = battery,
                        PowerStatus = powerStatus
                    });
                }

                BatteryWithStatusList = newStatuses;

                await InvokeAsync(StateHasChanged);

                await Task.Delay(5000);
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
            var height = ScreenInfo!.Height;
            var width = ScreenInfo!.Width;

            HandleResize(height - 300, width);
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
                var sessions = _batteriesService!.GetSessions();

                if (sessions != null)
                {
                    var now = _timeZoneService!.Now.Date;

                    await GetQuarterlyInfos();

                    TotalSolarPowerExpectedToday = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(now);
                    TotalSolarPowerExpectedTomorrow = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(now.AddDays(1));

                    TotalRevenueYesterday = await sessions.TotalCost(now.AddDays(-1));
                    TotalRevenueToday = await sessions.TotalCost(now);

                    BatteryPercentage = await _batteriesService.getBatteryPercentage();

                    BatteryMode = _batteriesService.GetBatteryMode();

                    await InvokeAsync(StateHasChanged);
                }
            });
        }

        /// <summary>
        /// The window is resized. Handle it.
        /// </summary>
        private void HandleResize(int height, int width)
        {
            ChangeChartStyle(height);
        }

        /// <summary>
        /// Retrieve all the quarterlyInfo objects within the selected date time and period.
        /// </summary>
        private async Task GetQuarterlyInfos()
        {
            DateTime selectedDate;

            if (!ShowAll || DateSelectionChosen == null)
                selectedDate = _timeZoneService!.Now;
            else
                selectedDate = DateSelectionChosen.Start!.Value;

            double averageSellingPrice = 0.0;
            double averageBuyingPrice = 0.0;

            var listFromBatteryService = _batteriesService?.GetQuarterlyInfos() ?? new();

            QuarterlyInfos = new List<QuarterlyInfoView>();

            if (listFromBatteryService.Count > 0)
            {
                averageSellingPrice = listFromBatteryService.Average(qi => qi.SellingPrice);
                averageBuyingPrice = listFromBatteryService.Average(qi => qi.BuyingPrice);
            }

            if (ShowAll)
            {
                var now = _timeZoneService!.Now;

                var quarterTime = now.DateFloorQuarter();

                var QuarterlyInfoList = listFromBatteryService?
                    .Where(hi => hi.Time >= quarterTime)
                    .ToList();

                var performanceList = await _performanceDataService!
                    .GetList(async (set) =>
                    {
                        var result = set.Where(c => c.Time >= selectedDate.Date && c.Time < quarterTime)
                                        .ToList();
                        return await Task.FromResult(result);
                    });

                foreach (var quarterlyInfo in QuarterlyInfoList ?? new List<QuarterlyInfo>())
                {
                    QuarterlyInfos?.Add(FillQuarterlyInfoView(quarterlyInfo, averageBuyingPrice, averageSellingPrice));
                }

                foreach (var performance in performanceList)
                {
                    QuarterlyInfos?.Add(FillQuarterlyInfoView(performance));
                }
            }
            else
            {
                var quarterlyInfoList = listFromBatteryService?
                    .Where(hi => hi.Time >= selectedDate.DateFloorQuarter())
                    .ToList();

                foreach (var quarterlyInfo in quarterlyInfoList!)
                {
                    QuarterlyInfos?.Add(FillQuarterlyInfoView(quarterlyInfo, averageBuyingPrice, averageSellingPrice));
                }
            }

            ChangeChartStyle(ScreenInfo!.Height - 300);

            await InvokeAsync(() => StateHasChanged());
        }

        public QuarterlyInfoView FillQuarterlyInfoView(QuarterlyInfo quarterlyInfo, double averageBuyingPrice, double averageSellingPrice)
        {
            return new QuarterlyInfoView
            {
                Time = quarterlyInfo.Time,
                SessionId = quarterlyInfo.SessionId,
                BuyingPrice = quarterlyInfo.BuyingPrice,
                SellingPrice = quarterlyInfo.SellingPrice,
                MarketPrice = quarterlyInfo.MarketPrice,
                Profit = quarterlyInfo.Profit,
                SmoothedBuyingPrice = quarterlyInfo.SmoothedBuyingPrice,
                VisualizeInChart = quarterlyInfo.VisualizeInChart,
                SmoothedSellingPrice = quarterlyInfo.SmoothedSellingPrice,
                ChargeLeft = quarterlyInfo.ChargeLeft,
                EstimatedConsumptionPerQuarterHour = quarterlyInfo.EstimatedConsumptionPerQuarterHour,
                SolarPowerPerQuarterHour = quarterlyInfo.SolarPowerPerQuarterHour,
                SolarGlobalRadiation = quarterlyInfo.SolarGlobalRadiation,
                ChargeLeftPercentage = quarterlyInfo.ChargeLeftPercentage,
                DisplayState = quarterlyInfo.DisplayState ?? string.Empty,
                Price = quarterlyInfo.Price,
                ChargeNeeded = quarterlyInfo.ChargeNeeded,
                SmoothedSolarPower = quarterlyInfo.SmoothedSolarPower,
                AverageBuyingPrice = averageBuyingPrice,
                AverageSellingPrice = averageSellingPrice,
            };
        }

        public QuarterlyInfoView FillQuarterlyInfoView(Performance performance)
        {
            return new QuarterlyInfoView
            {
                Time = performance.Time,
                SessionId = null,
                BuyingPrice = performance.BuyingPrice,
                SellingPrice = performance.SellingPrice,
                MarketPrice = performance.MarketPrice,
                Profit = performance.Profit,
                SmoothedBuyingPrice = performance.BuyingPrice,
                VisualizeInChart = performance.VisualizeInChart,
                SmoothedSellingPrice = performance.SellingPrice,
                ChargeLeft = performance.ChargeLeft,
                EstimatedConsumptionPerQuarterHour = performance.EstimatedConsumptionPerQuarterHour,
                SolarPowerPerQuarterHour = performance.SolarPowerPerQuarterHour,
                SolarGlobalRadiation = performance.SolarGlobalRadiation,
                ChargeLeftPercentage = performance.ChargeLeftPercentage,
                DisplayState = performance.DisplayState ?? string.Empty,
                Price = performance.Price,
                ChargeNeeded = performance.ChargeNeeded,
                SmoothedSolarPower = performance.SmoothedSolarPower
            };
        }

        public async Task SelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;

            await GetQuarterlyInfos();
        }


        public bool IsManualOverride => _batteriesService!.IsManualOverride;

        public bool WeAreInControl => _batteriesService!.WeAreInControl;

        public DateArgs DateSelectionChosen { get; private set; }

        /// <summary>
        /// Change the width of the chart depending on the number of quarterlyInfo objects.
        /// </summary>
        private void ChangeChartStyle(int height)
        {
            // 13 pixels per data row (3)
            var width = QuarterlyInfos?.Count * 3 * 13;

            GraphStyle = $"min-height: {height}px; width: {width}px; visibility: initial;";
        }

        /// <summary>
        /// Format the prices displayed in the X-axis.
        /// </summary>
        public string FormatAsPrice(object value)
        {
            if (value is double)
            {
                var price = (double)value;

                return $"{price:n2}";
            }

            return "";
        }

        /// <summary>
        /// Format the time displayed in the Y-axis.
        /// </summary>
        public string FormatAsDayHour(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}/{dateTime.Month} {dateTime.Hour}:{dateTime.Minute:00}";
            }

            return "";
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _batteriesService!.DataChanged -= BatteriesServiceDataChanged;
                _batteriesService!.OnHeartBeat -= HeartBeat;
            }

            base.Dispose();
        }
    }
}
