using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;

namespace SessyWeb.Components
{
    public partial class ChargingHoursTooltip
    {
        [Parameter]
        public HourlyInfo HourlyInfo { get; set; } = new HourlyInfo();
    }
}
