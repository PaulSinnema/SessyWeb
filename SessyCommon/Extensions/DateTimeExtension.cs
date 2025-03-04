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
    }
}
