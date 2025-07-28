using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;

namespace SessyWeb.Components
{
    public partial class ChargingHoursChartComponent : BaseComponent
    {
        [Parameter]
        public List<QuarterlyInfo>? HourlyInfos { get; set; }

        public string _graphStyle = "min-width: 250px; visibility: hidden;";

        [Parameter]
        public string GraphStyle 
        { 
            get => _graphStyle;
            set
            {
                if (_graphStyle != value)
                {
                    _graphStyle = value;

                    StateHasChanged();
                }
            }
        } 
    }
}
