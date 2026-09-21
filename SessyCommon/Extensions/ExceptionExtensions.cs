using System.Text;

namespace SessyCommon.Extensions
{

    public static class ExceptionExtensions
    {
        /// <summary>
        /// Formats an exception and its inner exceptions into a readable string.
        /// </summary>
        /// <param name="exception">The exception to format.</param>
        /// <returns>A formatted string with details about the exception and its inner exceptions.</returns>
        public static string ToDetailedString(this Exception? exception, string? message = null)
        {
            if (exception == null) return string.Empty;

            var sb = new StringBuilder();

            if (message != null)
            {
                sb.AppendLine($"{message}");
            }

            sb.AppendLine("Exception Details:");

            int level = 0;
            var currentException = exception;

            while (currentException != null)
            {
                sb.AppendLine($"Level {level}:");
                sb.AppendLine($"Type: {currentException.GetType().FullName}");
                sb.AppendLine($"Message: {currentException.Message}");
                sb.AppendLine($"Source: {currentException.Source}");
                sb.AppendLine($"Stack Trace: {currentException.StackTrace}");
                sb.AppendLine(new string('-', 50));

                currentException = currentException.InnerException;
                level++;
            }

            return sb.ToString();
        }

        /// <summary>The message of the innermost (root-cause) exception — concise, no stack trace.
        /// Use this for a user-facing "what really went wrong" instead of the wrapper's message.</summary>
        public static string RootMessage(this Exception? exception)
        {
            if (exception == null) return string.Empty;

            var current = exception;
            while (current.InnerException != null)
                current = current.InnerException;

            return current.Message;
        }
    }

}
