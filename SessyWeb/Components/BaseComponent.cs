using Microsoft.AspNetCore.Components;

namespace SessyWeb.Components
{
    public class BaseComponent : LayoutComponentBase
    {
        [CascadingParameter]
        public bool IsMobile { get; set; }

        [CascadingParameter]
        public bool IsLandscape { get; set; }

        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}
