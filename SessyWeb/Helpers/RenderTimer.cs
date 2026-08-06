using System.Diagnostics;

namespace SessyWeb.Helpers
{
    /// <summary>
    /// Measures how long a component's render actually takes on the circuit dispatcher.
    ///
    /// The clock in the header reports THAT the UI stalled; this reports WHICH component was busy.
    /// Start it where the render is requested, stop it in OnAfterRender — the span covers building
    /// the render tree, diffing it and handing the batch to SignalR, which for a chart of some
    /// seventeen series over a few hundred quarters is the expensive part.
    /// </summary>
    public sealed class RenderTimer
    {
        private readonly string _name;
        private readonly int _thresholdMs;
        private long _startedAt;

        public RenderTimer(string name, int thresholdMs = 250)
        {
            _name = name;
            _thresholdMs = thresholdMs;
        }

        public void Started() => _startedAt = Stopwatch.GetTimestamp();

        public void Finished()
        {
            if (_startedAt == 0) return;

            var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
            _startedAt = 0;

            if (elapsed >= _thresholdMs)
                Console.WriteLine($"Slow render: {_name} took {elapsed:F0} ms.");
        }
    }
}
