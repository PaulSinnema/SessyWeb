using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Extensions;
using SessyController.Configurations;
using SolCalc;
using SolCalc.Data;

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
                throw new InvalidOperationException("Time zone is missing in appsettings.json");
        }

        /// <summary>
        /// Gets the local time using the timezone set in appsettings.json.
        /// </summary>
#if DEBUG
        public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
#else
        public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
#endif

        /// <summary>
        /// Gets the sunlight level for time zone, latitude and longitude.
        /// </summary>
        public SunlightLevel GetSunlightLevel(double latitude, double longitude)
        {
            DateTimeZone zone = DateTimeZoneProviders.Tzdb[_settingsConfig.Timezone!];
            ZonedDateTime now = SystemClock.Instance.InZone(zone).GetCurrentZonedDateTime();


            return SunlightCalculator.GetSunlightAt(now, latitude, longitude);
        }
    }
}
