using Microsoft.AspNetCore.Components;

namespace SessyWeb.Components
{
    public class BaseComponent : ComponentBase
    {
        [CascadingParameter]
        public bool IsMobile { get; set; }

        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}
