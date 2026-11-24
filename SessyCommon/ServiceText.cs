using System.Globalization;

namespace SessyCommon
{
    /// <summary>
    /// Lets a service build its texts in the language of the page that asked for them. The page sets its
    /// translator around the call; the value flows with the async call only, so concurrent pages in other
    /// languages do not mix. Without a translator the English text is used.
    /// </summary>
    public static class ServiceText
    {
        private static readonly AsyncLocal<Func<string, string>?> Current = new();

        /// <summary>Uses the translator until the returned scope is disposed.</summary>
        public static IDisposable Use(Func<string, string> translate)
        {
            var previous = Current.Value;
            Current.Value = translate;
            return new Scope(previous);
        }

        /// <summary>The text in the current language; the English text is the key.</summary>
        public static string Tx(string text) => Current.Value?.Invoke(text) ?? text;

        /// <summary>The translated format string filled in with the current culture.</summary>
        public static string Tf(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Tx(format), args);

        private sealed class Scope(Func<string, string>? previous) : IDisposable
        {
            public void Dispose() => Current.Value = previous;
        }
    }
}
