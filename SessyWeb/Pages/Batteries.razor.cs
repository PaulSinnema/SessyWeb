using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class Batteries : PageBase
    {
        public List<Battery>? BatteriesList = new List<Battery>();

        private CancellationTokenSource? _cts { get; set; }

        protected async override void OnInitialized()
        {
            base.OnInitialized();

            // Start de timer als een aparte taak
            await StartBatteryUpdateLoop();
        }

        private async Task StartBatteryUpdateLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            _cts = new();

            try
            {
                do
                {
                    if (IsComponentActive)
                    {
                        // Take care of updating the UI in the render-thread
                        await InvokeAsync(() =>
                        {
                            BatteriesList = batteryContainer?.Batteries?.ToList();
                            StateHasChanged();
                        });
                    }
                }
                while (await timer.WaitForNextTickAsync(_cts!.Token));
            }
            catch (OperationCanceledException)
            {
                _cts.Cancel();
                _cts.Dispose();

                Console.WriteLine("Index: Timer stopped");
            }
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;

                base.Dispose();
            }
        }
    }
}
