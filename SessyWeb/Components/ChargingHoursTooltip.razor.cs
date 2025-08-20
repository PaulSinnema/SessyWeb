using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;
using static SessyWeb.Pages.ChargingHoursPage;

namespace SessyWeb.Components
{
    public partial class ChargingHoursTooltip : BaseComponent
    {
        [Parameter]
        public QuarterlyInfoView? HourlyInfo { get; set; }
    }
}
