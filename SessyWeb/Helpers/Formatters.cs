namespace SessyWeb.Helpers
{
    public class Formatters
    {
        /// <summary>
        /// Format the time displayed in the Y-axis.
        /// </summary>
        public static string FormatAsDayHour(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}/{dateTime.Month} {dateTime.Hour}:{dateTime.Minute:00}";
            }

            return "Not DateTime";
        }

        public static string FormatAsDate(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}/{dateTime.Month}/{dateTime.Year}";
            }

            return "Not DateTime";
        }

        /// <summary>
        /// Format the prices displayed in the X-axis.
        /// </summary>
        public static string FormatAsPrice(object value)
        {
            if (value is double)
            {
                var price = (double)value;

                return $"{price:n2}";
            }

            return "";
        }
    }
}
