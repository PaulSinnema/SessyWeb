using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using static SessyWeb.Pages.ChargingHoursPage;

namespace SessyWeb.Components
{
    public partial class ChargingHoursChartComponent : BaseComponent
    {
        [Parameter]
        public List<QuarterlyInfoView>? HourlyInfos { get; set; }

        public string _graphStyle = "min-width: 250px; visibility: hidden;";

        [Parameter]
        public string? GraphStyle { get; set; }

        public RadzenChart? QuarterlyHourChart { get; set; }
    }
}
