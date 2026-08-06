namespace SessyWeb.Components
{
    using Microsoft.AspNetCore.Components;
    using SessyCommon.Services;
    using SessyController.Services;
    using System.Timers;

    public partial class Clock : BaseComponent, IDisposable
    {
        [Inject] private ILogger<Clock>? _logger { get; set; }

        private string? CurrentTime { get; set; } = "";
        private System.Timers.Timer? Timer;

        /// <summary>
        /// How late a tick may be before it is reported. The clock ticks once a second on the
        /// circuit's dispatcher, so the delay between the timer firing and the work actually
        /// running IS the time the UI was unresponsive — the cheapest possible measurement of the
        /// symptom itself.
        /// </summary>
        private const int TickDelayWarningMs = 500;

        private bool _wasLate;

        protected override void OnInitialized()
        {
            Timer = new System.Timers.Timer(1000); // Update every second
            Timer.Elapsed += (sender, args) =>
            {
                if (_timeZoneService != null)
                {
                    var queuedAt = Environment.TickCount64;

                    InvokeAsync(() =>
                    {
                        var delay = Environment.TickCount64 - queuedAt;

                        // One line per block, not one per queued tick: the timer keeps firing while
                        // the dispatcher is stuck, so a single stall drains as a descending series
                        // (4469, 3469, 2469, ...). The first tick out of the queue carries the real
                        // duration; the rest are echoes of the same block.
                        if (delay >= TickDelayWarningMs)
                        {
                            if (!_wasLate)
                                _logger?.LogInformation($"UI blocked: clock tick ran {delay} ms late.");

                            _wasLate = true;
                        }
                        else
                            _wasLate = false;

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
