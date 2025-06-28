using Microsoft.AspNetCore.Components;
using Radzen;
using SessyCommon.Extensions;
using SessyController.Services;
using SessyController.Services.Items;
using static SessyController.Services.NetZeroHomeService;

namespace SessyWeb.Pages
{
    public partial class ChargingHoursPage : PageBase
    {
        [Inject]
        public TooltipService? tooltipService { get; set; }
        [Inject]
        public BatteriesService? _batteriesService { get; set; }
        [Inject]
        public SolarService? _solarService { get; set; }
        [Inject]
        public TimeZoneService? _timeZoneService { get; set; }
        [Inject]
        public NetZeroHomeService? _netZeroHomeService { get; set; }

        public PowerInformationContainer? PowerInformation { get; set; }

        public List<HourlyInfo>? HourlyInfos { get; set; } = new List<HourlyInfo>();

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

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();

            GetOnlyCurrentHourlyInfos();

            try
            {
                await InvokeAsync(async () => await HandleScreenHeight());

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
        /// Get the statusses of the batteries in a loop.
        /// </summary>
        private async Task UpdateLoop()
        {
            while (true)
            {
                var newStatuses = new List<BatteryWithStatus>();

                foreach (var battery in batteryContainer!.Batteries!)
                {
                    var systemState = await battery.GetPowerStatus();
                    var powerStatus = await battery.GetPowerStatus();

                    newStatuses.Add(new BatteryWithStatus
                    {
                        Battery = battery,
                        PowerStatus = powerStatus
                    });

                    PowerInformation = _netZeroHomeService!.PowerInformation;

                    StateHasChanged();
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
                _screenSizeService!.OnScreenSizeChanged += HandleResize;
                _batteriesService!.DataChanged += BatteriesServiceDataChanged;
                _batteriesService!.OnHeartBeat += HeartBeat;

                await _screenSizeService.InitializeAsync();

                await InvokeAsync(async () =>
                {
                    await HandleScreenHeight();

                    await BatteriesServiceDataChanged();

                    StateHasChanged();
                });
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        /// <summary>
        /// Handle the height of the screen.
        /// </summary>
        private async Task HandleScreenHeight()
        {
            var height = await _screenSizeService!.GetScreenHeightAsync();
            var width = await _screenSizeService.GetScreenWidthAsync();

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
                StateHasChanged();

                await Task.Delay(3000).ContinueWith(_ =>
                {
                    IsBeating = false;
                    InvokeAsync(StateHasChanged);
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
                    var now = _timeZoneService!.Now;

                    await HandleScreenHeight();

                    TotalSolarPowerExpectedToday = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(now);
                    TotalSolarPowerExpectedTomorrow = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(now.AddDays(1));

                    TotalRevenueYesterday = sessions.TotalCost(now.AddDays(-1));
                    TotalRevenueToday = sessions.TotalCost(now);

                    BatteryPercentage = await _batteriesService.getBatteryPercentage();

                    BatteryMode = _batteriesService.GetBatteryMode();

                    GetOnlyCurrentHourlyInfos();

                    StateHasChanged();
                }
            });
        }

        /// <summary>
        /// The window is resized. Hanle it.
        /// </summary>
        private async void HandleResize(int height, int width)
        {
            await InvokeAsync(() =>
            {
                ChangeChartStyle(height);
            });
        }

        /// <summary>
        /// Retrieve all the hourlyInfo objects but only the current and future ones.
        /// </summary>
        private void GetOnlyCurrentHourlyInfos()
        {
            var now = _timeZoneService!.Now;

            HourlyInfos = _batteriesService?.GetHourlyInfos()?
                .Where(hi => hi.Time >= now.DateFloorQuarter())
                .ToList();
        }

        public bool IsManualOverride => _batteriesService!.IsManualOverride;

        public bool WeAreInControl => _batteriesService!.WeAreInControl;

        /// <summary>
        /// Change the width of the chart depending on the number of hourlyInfo objects.
        /// </summary>
        private void ChangeChartStyle(int height)
        {
            // 25 pixels per data row (3)
            var width = HourlyInfos?.Count * 3 * 13;

            GraphStyle = $"min-height: {height}px; width: {width}px; visibility: initial;";

            StateHasChanged();
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
                _screenSizeService!.OnScreenSizeChanged -= HandleResize;
                _batteriesService!.DataChanged -= BatteriesServiceDataChanged;
                _batteriesService!.OnHeartBeat -= HeartBeat;
            }

            base.Dispose();
        }
    }
}
