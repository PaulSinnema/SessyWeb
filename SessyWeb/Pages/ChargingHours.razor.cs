using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;

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

        public List<HourlyInfo>? HourlyInfos { get; set; } = new List<HourlyInfo>();

        public double TotalSolarPowerExpected { get; set; }

        private CancellationTokenSource _cts = new();

        private string RowHeightStyle { get; set; } = "height 20px";
        private string GraphStyle { get; set; } = "height: 100%; min-width: 250px; visibility: hidden;";

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();

            HourlyInfos = _batteriesService?.GetHourlyInfos();

            await StartLoop();
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
                        await InvokeAsync(() =>
                        {
                            var now = _timeZoneService.Now;

                            HourlyInfos = _batteriesService?.GetHourlyInfos()?
                                .Where(hi => hi.Time >= now.Date.AddHours(now.Hour))
                                .ToList();

                            TotalSolarPowerExpected = _solarService == null ? 0.0 : _solarService.GetTotalSolarPowerExpected(HourlyInfos);

                            // 20 pixels per data row (5)
                            var height = HourlyInfos?.Count * 5 * 20;

                            GraphStyle = $"min-height: 800px; min-width: 250px; width: {height}px; visibility: initial;";

                            StateHasChanged();
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
    }
}
