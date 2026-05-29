using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Extensions;
using SessyCommon.Configurations;
using SolCalc;
using SolCalc.Data;

namespace SessyCommon.Services
{
    public class TimeZoneService
    {
        private string _currentTimezone;
        private static TimeZoneInfo? _timeZone;

        public TimeZoneInfo TimeZone => _timeZone!;

        public TimeZoneService(IOptions<SettingsConfig> settingsConfig)
        {
            _currentTimezone = settingsConfig.Value?.Timezone ?? "Europe/Amsterdam";
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_currentTimezone);
        }

        /// <summary>
        /// Updates the active timezone at runtime when settings change.
        /// Called via the SettingsChanged event subscription in Program.cs.
        /// </summary>
        public void UpdateTimezone(string? timezone)
        {
            if (string.IsNullOrWhiteSpace(timezone) || timezone == _currentTimezone)
                return;

            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            _currentTimezone = timezone;
        }

        /// <summary>
        /// Gets the local time using the configured timezone.
        /// Virtual so it can be overridden in unit tests via Moq.
        /// </summary>
#if DEBUG
        public virtual DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone!).AddMinutes(-7);
#else
        public virtual DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone!);
#endif

        /// <summary>Gets the sunlight level for the given coordinates.</summary>
        public SunlightLevel GetSunlightLevel(double latitude, double longitude)
        {
            DateTimeZone zone = DateTimeZoneProviders.Tzdb[_currentTimezone];
            ZonedDateTime now = SystemClock.Instance.InZone(zone).GetCurrentZonedDateTime();
            return SunlightCalculator.GetSunlightAt(now, latitude, longitude);
        }

        public static DateTime FromUnixTime(long unixTime)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime, _timeZone!);
        }

        public static DateTime FromUnixTime(long? unixTime)
        {
            return unixTime.HasValue ? FromUnixTime(unixTime.Value) : DateTime.MinValue;
        }
    }
}