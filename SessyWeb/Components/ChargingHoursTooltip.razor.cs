using Microsoft.AspNetCore.Components;
using SessyWeb.Pages;

namespace SessyWeb.Components
{
    public partial class ChargingHoursTooltip : BaseComponent
    {
        [Parameter]
        public QuarterlyInfoView? QuarterlyInfo { get; set; }
    }
}
