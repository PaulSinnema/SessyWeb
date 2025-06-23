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
    }
}
