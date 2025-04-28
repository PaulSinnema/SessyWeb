namespace SessyCommon.Extensions
{
    public static class DateTimeExtension
    {
        /// <summary>
        /// Returns the date and time without the minutes and seconds.
        /// </summary>
        public static DateTime DateHour(this DateTime time)
        {
            return time.Date.AddHours(time.Hour);
        }

        public static DateTime DateNearestQuarter(this DateTime time)
        {
            const long ticksPerQuarter = TimeSpan.TicksPerMinute * 15; // 15 minutes in ticks
            long remainder = time.Ticks % ticksPerQuarter;

            if (remainder == 0)
                return time; // already on a boundary

            long half = ticksPerQuarter / 2;
            long adjustment = remainder < half ? -remainder : (ticksPerQuarter - remainder);

            return new DateTime(time.Ticks + adjustment, time.Kind);
        }

        private static readonly long QuarterHourTicks = TimeSpan.FromMinutes(15).Ticks;

        public static DateTime DateFloorQuarter(this DateTime value)
        {
            long ticksFloored = value.Ticks - (value.Ticks % QuarterHourTicks);
            return new DateTime(ticksFloored, value.Kind);
        }
    }
}
