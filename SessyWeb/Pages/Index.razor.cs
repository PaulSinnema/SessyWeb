using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class Index : PageBase
    {
        public List<Battery>? Batteries = new List<Battery>();

        private CancellationTokenSource? _cts;

        protected async override void OnInitialized()
        {
            _cts = new();

            // Laad initiële data
            Batteries = batteryContainer?.Batteries?.ToList();

            // Start de timer als een aparte taak
            await StartBatteryUpdateLoop();
        }

        private async Task StartBatteryUpdateLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                while (await timer.WaitForNextTickAsync(_cts!.Token))
                {
                    if (IsComponentActive)
                    {
                        // Take care of updating the UI in the render-thread
                        await InvokeAsync(() =>
                        {
                            Batteries = batteryContainer?.Batteries?.ToList();
                            StateHasChanged();
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Index: Timer stopped");
            }
        }

        public override void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
