using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class Index : PageBase
    {
        public List<Battery>? Batteries = new List<Battery>();

        private CancellationTokenSource _cts = new();

        protected override async Task OnInitializedAsync()
        {
            // Laad initiële data
            Batteries = batteryContainer?.Batteries?.ToList();

            // Start de timer als een aparte taak
            _ = StartBatteryUpdateLoop();
        }

        private async Task StartBatteryUpdateLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    Console.WriteLine("Timer tick - Batterijen verversen");

                    // Zorg ervoor dat de UI wordt bijgewerkt in de render-thread
                    await InvokeAsync(() =>
                    {
                        Batteries = batteryContainer?.Batteries?.ToList();
                        StateHasChanged();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Timer gestopt.");
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
