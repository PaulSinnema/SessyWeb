using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Services;

namespace SessyWeb.Pages
{
    public partial class ChargingHours : PageBase
    {
        [Inject]
        public BatteriesService? _batteriesService { get; set; }
        [Inject]
        public SolarService? _solarService { get; set; }
        [Inject]
        public TimeZoneService? _timeZoneService { get; set; }
        [Inject]
        public ScreenSizeService? _screenSizeService { get; set; }

        public List<HourlyInfo>? HourlyInfos { get; set; } = new List<HourlyInfo>();

        public double TotalSolarPowerExpected { get; set; }

        public string TotalSolarPowerExpectedVisual => TotalSolarPowerExpected.ToString("0.###");

        private CancellationTokenSource _cts = new();

        private string RowHeightStyle { get; set; } = "height 20px";
        private string GraphStyle { get; set; } = "min-width: 250px; visibility: hidden;";

        protected override async Task OnInitializedAsync()
        {
            _screenSizeService!.OnScreenHeightChanged += HandleResize;

            await base.OnInitializedAsync();

            HourlyInfos = _batteriesService?.GetHourlyInfos();

            await StartLoop();
        }

        private async void HandleResize(int height)
        {
            await ChangeChartStyle();
            StateHasChanged();
        }

        private async Task StartLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                do
                {
                    if (IsComponentActive)
                    {
                        await InvokeAsync(async () =>
                        {
                            var now = _timeZoneService!.Now;

                            HourlyInfos = _batteriesService?.GetHourlyInfos()?
                                .Where(hi => hi.Time >= now.Date.AddHours(now.Hour - 1))
                                .ToList();

                            TotalSolarPowerExpected = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(HourlyInfos);

                            await ChangeChartStyle();
                        });
                    }
                }
                while (await timer.WaitForNextTickAsync(_cts.Token));
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Charging hours page: Timer stopped.");
            }
        }

        private async Task ChangeChartStyle()
        {
            // 25 pixels per data row (3)
            var width = HourlyInfos?.Count * 3 * 25;
            var height = await _screenSizeService!.GetScreenHeightAsync();

            GraphStyle = $"min-height: {height - 250}px; width: {width}px; visibility: initial;";

            StateHasChanged();
        }

        public string FormatAsPrice(object value)
        {
            if (value is double)
            {
                var price = (double)value;

                return $"{price:c3}";
            }

            return "";
        }

        public string FormatAsDayHour(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}-{dateTime.Month}/{dateTime.Hour}";
            }

            return "";
        }

        public override void Dispose()
        {
            _screenSizeService!.OnScreenHeightChanged -= HandleResize;

            base.Dispose();
        }
    }
}
