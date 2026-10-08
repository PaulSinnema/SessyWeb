using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;
using SessyData.Model;

namespace SessyWeb.Pages
{
    public partial class BatteriesPage : PageBase
    {
        public List<Battery>? BatteriesList = new List<Battery>();

        [Inject]
        public GridTargetService? GridTargetService { get; set; }

        [Inject]
        public SettingsService? SettingsService { get; set; }

        // Setpoint per battery: no grid target in use.
        public bool UseSetpoints => SettingsService?.Current.BatteryControlMethod == BatteryControlMethod.BatterySetpoint;

        // Grid target (W) SessyWeb computes for the P1 meter, refreshed each tick. Import +, export -.
        // In DEBUG this is the would-be value; nothing is written to the meter.
        public int? GridTargetW;

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
                            GridTargetW = GridTargetService?.LastComputedTargetW;
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
