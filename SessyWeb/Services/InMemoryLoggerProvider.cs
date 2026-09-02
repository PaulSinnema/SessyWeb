using Microsoft.Extensions.Logging;

namespace SessyWeb.Services
{
    /// <summary>
    /// Logging provider that mirrors every log entry into <see cref="LogBufferService"/>, formatted
    /// much like the console logger, so the Settings page can show the same lines that go to the
    /// container log. Added alongside the console provider in Program.cs.
    /// </summary>
    public sealed class InMemoryLoggerProvider : ILoggerProvider
    {
        private readonly LogBufferService _buffer;

        public InMemoryLoggerProvider(LogBufferService buffer) => _buffer = buffer;

        public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, _buffer);

        public void Dispose() { }

        private sealed class InMemoryLogger : ILogger
        {
            private readonly string _category;
            private readonly LogBufferService _buffer;

            public InMemoryLogger(string category, LogBufferService buffer)
            {
                // Keep only the last namespace segment so lines stay readable.
                var dot = category.LastIndexOf('.');
                _category = dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
                _buffer = buffer;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var message = formatter(state, exception);
                var line = $"{DateTime.Now:HH:mm:ss} {LevelTag(logLevel)} {_category}: {message}";

                if (exception != null)
                    line += Environment.NewLine + exception;

                _buffer.Append(logLevel, line);
            }

            private static string LevelTag(LogLevel level) => level switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => "    "
            };
        }
    }
}
