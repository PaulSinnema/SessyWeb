namespace SessyCommon.Extensions
{
    public static class DateTimeExtension
    {
        private static readonly long QuarterHourTicks = TimeSpan.FromMinutes(15).Ticks;

        /// <summary>
        /// Returns the date and time without the minutes and seconds.
        /// </summary>
        public static DateTime DateHour(this DateTime time)
        {
            return time.Date.AddHours(time.Hour);
        }

        public static DateTime DateNearestQuarter(this DateTime time)
        {
            long remainder = time.Ticks % QuarterHourTicks;

            if (remainder == 0)
                return time; // already on a boundary

            long half = QuarterHourTicks / 2;
            long adjustment = remainder < half ? -remainder : (QuarterHourTicks - remainder);

            return new DateTime(time.Ticks + adjustment, time.Kind);
        }

        public static DateTime DateFloorQuarter(this DateTime value)
        {
            long ticksFloored = value.Ticks - (value.Ticks % QuarterHourTicks);
            return new DateTime(ticksFloored, value.Kind);
        }

        public static DateTime DateCeilingQuarter(this DateTime value)
        {
            var ticksToNextQuarter = (QuarterHourTicks - (value.Ticks % QuarterHourTicks));
            long ticksCeiling = value.Ticks + (ticksToNextQuarter == QuarterHourTicks ? 0 : ticksToNextQuarter);
            return new DateTime(ticksCeiling, value.Kind);
        }

        /// <summary>
        /// Mogelijke resoluties binnen een tijdreeks.
        /// </summary>
        public enum TimeResolution
        {
            Unknown = 0,
            FifteenMinutes = 15,
            SixtyMinutes = 60
        }

        /// <summary>
        /// Detect resolution of the dates.
        /// </summary>
        /// <exception cref="ArgumentNullException"></exception>
        public static TimeResolution GetTimeResolution(this IEnumerable<DateTime> dates)
        {
            if (dates == null) throw new ArgumentNullException(nameof(dates));

            var ordered = dates.OrderBy(d => d).ToArray();
            if (ordered.Length < 2) return TimeResolution.Unknown;

            var deltas = new List<int>(ordered.Length - 1);

            for (int i = 1; i < ordered.Length; i++)
            {
                var deltaMinutes = (int)(ordered[i] - ordered[i - 1]).TotalMinutes;

                // nul of negatieve intervallen zijn ongeldig
                if (deltaMinutes <= 0) return TimeResolution.Unknown;

                deltas.Add(deltaMinutes);
            }

            return deltas.All(m => m == 60) ? TimeResolution.SixtyMinutes
                 : deltas.All(m => m == 15) ? TimeResolution.FifteenMinutes
                 : TimeResolution.Unknown;
        }
    }
}
