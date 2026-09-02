using System.Text;
using Microsoft.Extensions.Logging;

namespace SessyWeb.Services
{
    /// <summary>
    /// Bounded, thread-safe ring buffer of the most recent log lines. It is fed by
    /// <see cref="InMemoryLoggerProvider"/>, which mirrors everything the application writes through
    /// ILogger (the same stream that ends up in the container's stdout / <c>docker logs</c>), and is
    /// read by the Settings page so the container log can be shown live in the browser.
    ///
    /// Each line keeps its <see cref="LogLevel"/> so the viewer can filter by minimum level.
    /// Registered as a singleton and shared with the logging provider, so a single instance holds
    /// every component's output.
    /// </summary>
    public sealed class LogBufferService
    {
        /// <summary>Hard cap on the number of retained lines. Oldest lines are dropped first.</summary>
        public const int MaxLines = 1000;

        private readonly record struct Entry(LogLevel Level, string Text);

        private readonly object _lock = new();
        private readonly LinkedList<Entry> _lines = new();

        /// <summary>
        /// Increments on every change (append or clear), so a reader can cheaply tell whether
        /// anything is new without copying the whole buffer.
        /// </summary>
        public long Version { get; private set; }

        /// <summary>Raised after each change. Handlers must be quick and must not throw.</summary>
        public event Action? Changed;

        /// <summary>Appends one formatted line, dropping the oldest when over <see cref="MaxLines"/>.</summary>
        public void Append(LogLevel level, string line)
        {
            lock (_lock)
            {
                _lines.AddLast(new Entry(level, line));
                while (_lines.Count > MaxLines)
                    _lines.RemoveFirst();

                Version++;
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// The buffer as one string, oldest line first, keeping only lines at or above
        /// <paramref name="minLevel"/>.
        /// </summary>
        public string Snapshot(LogLevel minLevel = LogLevel.Trace)
        {
            lock (_lock)
            {
                var sb = new StringBuilder(_lines.Count * 96);
                foreach (var entry in _lines)
                    if (entry.Level >= minLevel)
                        sb.AppendLine(entry.Text);
                return sb.ToString();
            }
        }

        /// <summary>Empties the buffer (only the in-memory copy; the container log is untouched).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
                Version++;
            }

            Changed?.Invoke();
        }
    }
}
