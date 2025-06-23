using Microsoft.AspNetCore.Components;
using SessyWeb.Helpers;

namespace SessyWeb.Components
{
    public class BaseComponent : ComponentBase
    {
        [CascadingParameter(Name = "ScreenInfo")]
        public ScreenInfo? ScreenInfo{ get; set; }

        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}
