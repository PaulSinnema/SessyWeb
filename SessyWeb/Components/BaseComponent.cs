using Microsoft.AspNetCore.Components;

namespace SessyWeb.Components
{
    public class BaseComponent : ComponentBase
    {
        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}
