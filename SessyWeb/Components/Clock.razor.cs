namespace SessyWeb.Components
{
    using Microsoft.AspNetCore.Components;
    using SessyCommon.Services;
    using SessyController.Services;
    using System.Timers;

    public partial class Clock : BaseComponent, IDisposable
    {
        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private string? CurrentTime { get; set; } = "";
        private System.Timers.Timer? Timer;

        protected override void OnInitialized()
        {
            Timer = new System.Timers.Timer(1000); // Update every second
            Timer.Elapsed += (sender, args) =>
            {
                if (_timeZoneService != null)
                {
                    InvokeAsync(() =>
                    {
                        CurrentTime = _timeZoneService!.Now.ToString("dd MMMM yyyy  HH:mm:ss");
                        StateHasChanged();
                    });
                }
            };
            Timer.Start();
        }

        public void Dispose()
        {
            Timer?.Stop();
            Timer?.Dispose();
        }
    }
}
