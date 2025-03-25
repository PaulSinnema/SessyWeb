using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class BatteriesPage : PageBase
    {
        public List<Battery>? BatteriesList = new List<Battery>();

        private CancellationTokenSource _cts = new();

        protected override void OnInitialized()
        {
            base.OnInitialized();

            // Start de timer als een aparte taak
            Task.Run(async () => await StartBatteryUpdateLoop());
        }

        private async Task StartBatteryUpdateLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

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
                Console.WriteLine("Batteries page: Timer stopped");
            }
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _cts.Cancel();
                _cts.Dispose();

                _isDisposed = true;

                base.Dispose();
            }
        }
    }
}
