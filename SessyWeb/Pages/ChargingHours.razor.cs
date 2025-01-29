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

        public double TotalSolarPowerExpected => SolarService == null ? 0.0 : SolarService.GetTotalSolarPowerExpected(HourlyInfos);

        private CancellationTokenSource _cts = new();

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
                    // Zorg ervoor dat de UI wordt bijgewerkt in de render-thread
                    await InvokeAsync(() =>
                    {
                        HourlyInfos = BatteriesService?.GetHourlyInfos()?.ToList();
                        StateHasChanged();
                    });
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
