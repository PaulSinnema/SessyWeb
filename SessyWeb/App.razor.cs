using Microsoft.AspNetCore.Components;
using Radzen;

namespace SessyWeb
{
    public partial class App
    {
        [CascadingParameter]
        private HttpContext? HttpContext { get; set; }

        [Inject]
        private ThemeService? ThemeService { get; set; }

        protected override void OnInitialized()
        {
            base.OnInitialized();

            if (HttpContext != null)
            {
                var theme = HttpContext.Request.Cookies["SessyTheme"];

                if (!string.IsNullOrEmpty(theme) && ThemeService != null)
                {
                    ThemeService.SetTheme(theme, false);
                }
            }
        }
    }
}
