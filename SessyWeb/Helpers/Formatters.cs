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

                return $"{dateTime:dd/MM HH}h";
            }

            return "No DateTime";
        }

        public static string FormatAsDate(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}/{dateTime.Month}/{dateTime.Year}";
            }

            return "No DateTime";
        }

        public static string FormatAsDay(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime:dd MMM}";
            }

            return "No DateTime";
        }

        public static string FormatAsMonth(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime:MMM yyyy}";
            }

            return "No DateTime";
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
