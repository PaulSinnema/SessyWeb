using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;

namespace SessyWeb.Components
{
    public partial class ChargingHoursTooltip : BaseComponent
    {
        [Parameter]
        public QuarterlyInfo? HourlyInfo { get; set; }
    }
}
