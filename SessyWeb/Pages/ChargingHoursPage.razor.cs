using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Services;

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
        public ScreenSizeService? _screenSizeService { get; set; }

        public List<HourlyInfo>? HourlyInfos { get; set; } = new List<HourlyInfo>();

        public double TotalSolarPowerExpectedToday { get; private set; }
        public double TotalSolarPowerExpectedTomorrow { get; private set; }

        public string TotalSolarPowerExpectedTodayVisual => TotalSolarPowerExpectedToday.ToString("0.###");
        public string TotalSolarPowerExpectedTomorrowVisual => TotalSolarPowerExpectedTomorrow.ToString("0.###");

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
            }
            catch (Exception)
            {
                // Keep it silent.
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                _screenSizeService!.OnScreenHeightChanged += HandleResize;
                _batteriesService!.DataChanged += BatteriesServiceDataChanged;
                _batteriesService!.OnHeartBeat += HeartBeat;

                await _screenSizeService.InitializeAsync();

                await InvokeAsync(async () =>
                {
                    await HandleScreenHeight();

                    StateHasChanged();
                });
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        private async Task HandleScreenHeight()
        {
            var height = await _screenSizeService!.GetScreenHeightAsync();

            HandleResize(height);
        }

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

        private async Task BatteriesServiceDataChanged()
        {
            await InvokeAsync(async () =>
            {
                var now = _timeZoneService!.Now;

                await HandleScreenHeight();

                TotalSolarPowerExpectedToday = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(now);
                TotalSolarPowerExpectedTomorrow = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(now.AddDays(1));

                GetOnlyCurrentHourlyInfos();

                StateHasChanged();
            });
        }

        private async void HandleResize(int height)
        {
            await InvokeAsync(() =>
            {
                ChangeChartStyle(height);
            });
        }

        private void GetOnlyCurrentHourlyInfos()
        {
            var now = _timeZoneService!.Now;

            HourlyInfos = _batteriesService?.GetHourlyInfos()?
                .Where(hi => hi.Time >= now.Date.AddHours(now.Hour - 1))
                .ToList();
        }


        private void ChangeChartStyle(int height)
        {
            // 25 pixels per data row (3)
            var width = HourlyInfos?.Count * 3 * 25;

            GraphStyle = $"min-height: {height - 250}px; width: {width}px; visibility: initial;";
        }

        public string FormatAsPrice(object value)
        {
            if (value is double)
            {
                var price = (double)value;

                return $"{price:n5}";
            }

            return "";
        }

        public string FormatAsDayHour(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}/{dateTime.Month} {dateTime.Hour}u";
            }

            return "";
        }

        public void ShowExplanation()
        {

        }

        public override void Dispose()
        {
            _screenSizeService!.OnScreenHeightChanged -= HandleResize;
            _batteriesService!.DataChanged -= BatteriesServiceDataChanged;
            _batteriesService!.OnHeartBeat -= HeartBeat;

            base.Dispose();
        }
    }
}
