using System.Globalization;

namespace SessyWeb.Helpers
{
    public class Formatters
    {
        public static string FormatValue(object value)
        {
            return $"{value}";
        }

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

        /// <summary>
        /// Format the time displayed in the Y-axis.
        /// </summary>
        public static string FormatAsDayHourMinutes(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime:dd/MM HH:mm}";
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
            int month = 1;

            if (value is Int16)
            {
                month = Convert.ToInt32(value);
            }
            else if (value is DateTime)
            {
                var dateTime = (DateTime)value;
                month = dateTime.Month;
            }
            else
                throw new InvalidOperationException($"Object type not supported {value}");

            if (month >= 1 && month <= 13)
            {
                var culture = new CultureInfo(CultureInfo.CurrentCulture.Name);

                return culture.DateTimeFormat.GetAbbreviatedMonthName(month);
            }

            return $"Wrong {value}";
        }

        public static string FormatAsYear(object value)
        {
            if (value is double)
            {
                var doubleValue = (double)value;

                return $"{doubleValue}";
            }

            return "No double";
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

        /// <summary>
        /// Format the prices displayed in the X-axis.
        /// </summary>
        public static string FormatAsWatt(object value)
        {
            if (value is double)
            {
                var kwh = (double)value;

                return $"{kwh:n0}W";
            }

            return "";
        }

        /// <summary>
        /// Format the prices displayed in the X-axis.
        /// </summary>
        public static string FormatAsRoundedNumberWithZeroSuppression(object value)
        {
            return FormatDoubleAsRoundedNumberWithZeroSuppression(value, true);
        }

        /// <summary>
        /// Format the prices displayed in the X-axis.
        /// </summary>
        public static string FormatAsRoundedNumber(object value)
        {
            if (value is double)
            {
                return FormatDoubleAsRoundedNumberWithZeroSuppression(value, false);
            }

            return "";
        }

        private static string FormatDoubleAsRoundedNumberWithZeroSuppression(object value, bool suppressZero)
        {
            var kwh = (double)value;

            if (suppressZero && kwh == 0)
            {
                return "";
            }

            return $"{kwh:n0}";
        }
    }
}
