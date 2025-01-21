namespace SessyController.Services
{
    /// <summary>
    /// This class is used for logging information.
    /// </summary>

    /// <typeparam name="T"></typeparam>
    public class LoggingService<T> where T : class
    {
        private readonly ILogger<T> _logger;

        public LoggingService(ILogger<T> logger)
        {
            _logger = logger;
        }

        public void LogException(Exception ex, string? message)
        {
            if (_logger != null)
                _logger.LogError(ex, message);
            else
                throw new Exception(message);
        }


        public void LogError(string? message)
        {
            if (_logger != null)
                _logger.LogError(message);
            else
                throw new Exception(message);
        }

        public void LogInformation(string? message)
        {
            if (_logger != null)
                _logger.LogInformation(message);
            else
                throw new Exception(message);
        }

        public void LogWarning(string? message)
        {
            if (_logger != null)
                _logger.LogWarning(message);
            else
                throw new Exception(message);
        }
    }
}
