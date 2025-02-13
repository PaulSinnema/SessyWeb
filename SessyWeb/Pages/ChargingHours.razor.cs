using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class ChargingHours : PageBase
    {
        [Inject]
        public BatteriesService? BatteriesService { get; set; }
        [Inject]
        public SolarService? SolarService { get; set; }

        public List<HourlyInfo>? HourlyInfos { get; set; } = new List<HourlyInfo>();

        public double TotalSolarPowerExpected { get; set; }

        private CancellationTokenSource _cts = new();

        private string RowHeightStyle { get; set; } = "height 20px";
        private string GraphStyle { get; set; } = "height: 100%; min-width: 250px; visibility: hidden;";

        protected override async Task OnInitializedAsync()
        {
            HourlyInfos = BatteriesService?.GetHourlyInfos();

            await StartLoop();

            await base.OnInitializedAsync();
        }

        private async Task StartLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    if (IsComponentActive)
                    {
                        await InvokeAsync(() =>
                        {
                            HourlyInfos = BatteriesService?.GetHourlyInfos()?.ToList();

                            TotalSolarPowerExpected = SolarService == null ? 0.0 : SolarService.GetTotalSolarPowerExpected(HourlyInfos);

                            // 20 pixels per data row (5)
                            var height = HourlyInfos?.Count * 5 * 20;

                            GraphStyle = $"min-height: 1000px; min-width: 250px; visibility: initial;";

                            StateHasChanged();
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Timer gestopt.");
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
