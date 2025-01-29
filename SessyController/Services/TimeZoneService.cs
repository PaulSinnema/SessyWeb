using Microsoft.Extensions.Options;
using SessyController.Configurations;

namespace SessyController.Services
{
    public class TimeZoneService
    {
        private SettingsConfig _settingsConfig { get; set; }

        private TimeZoneInfo _timeZone { get; set; }

        public TimeZoneService(IOptions<SettingsConfig> settingsConfig)
        {
            _settingsConfig = settingsConfig.Value;

            if (!string.IsNullOrWhiteSpace(_settingsConfig?.Timezone))
                _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_settingsConfig.Timezone);
            else
                throw new InvalidOperationException("Timezone is missing");
        }

        /// <summary>
        /// Gets the local time using the timezone set in appsettings.json.
        /// </summary>
        public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
    }
}
