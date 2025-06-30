using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;

namespace SessyWeb.Components
{

    public partial class BatteryInfo : BaseComponent
    {
        [Parameter]
        public Battery? Battery { get; set; }

        public PowerStatus? powerStatus { get; set; }

        private CancellationTokenSource _cts = new();

        public ActivePowerStrategy? ActivePowerStrategy { get; set; }

        protected override async Task OnInitializedAsync()
        {
            await LoadPowerStatus(); // Directe eerste update

            _ = StartUpdateLoop();
        }

        private async Task StartUpdateLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    await LoadPowerStatus();
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Ophalen van power status gestopt.");
            }
        }

        private async Task LoadPowerStatus()
        {
            if (Battery != null)
            {
                powerStatus = await Battery.GetPowerStatus();

                ActivePowerStrategy = await Battery.GetActivePowerStrategy();
                await InvokeAsync(StateHasChanged);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
