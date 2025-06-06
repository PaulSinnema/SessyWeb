using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;

namespace SessyWeb.Components
{
    public partial class ChargingHoursChartComponent : BaseComponent
    {
        [Parameter]
        public List<HourlyInfo>? HourlyInfos { get; set; }
        [Parameter]
        public string GraphStyle { get; set; } = "min-width: 250px; visibility: hidden;";

        /// <summary>
        /// Format the time displayed in the Y-axis.
        /// </summary>
        public string FormatAsDayHour(object value)
        {
            if (value is DateTime)
            {
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}/{dateTime.Month} {dateTime.Hour}:{dateTime.Minute:00}";
            }

            return "";
        }

        /// <summary>
        /// Format the prices displayed in the X-axis.
        /// </summary>
        public string FormatAsPrice(object value)
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
