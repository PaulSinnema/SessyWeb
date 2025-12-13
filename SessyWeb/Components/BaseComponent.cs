using Microsoft.AspNetCore.Components;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Helpers;

namespace SessyWeb.Components
{
    public class BaseComponent : ComponentBase
    {
        [Inject]
        public TimeZoneService? _timeZoneService { get; set; }

        [Inject]
        public BatteryContainer? batteryContainer { get; set; }

        [Inject]
        public BatteriesService? _batteriesService { get; set; }

        [CascadingParameter(Name = "ScreenInfo")]
        public ScreenInfo? ScreenInfo{ get; set; }

        public bool IsManualOverride => _batteriesService!.IsManualOverride;

        public bool WeAreInControl => _batteriesService!.WeAreInControl;



        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}
