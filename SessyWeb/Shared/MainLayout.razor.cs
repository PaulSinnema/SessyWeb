using Microsoft.AspNetCore.Mvc;

namespace SessyWeb.Shared
{
    public partial class MainLayout
    {
        public static string NewTheme { get; set; } = "Dark";

        bool sidebar1Expanded = true;

        [IgnoreAntiforgeryToken]
        void ChangeTheme(string theme)
        {
            NewTheme = theme;
            ThemeService.SetTheme(theme, true);
        }

        public void CollapseMenu()
        {
            sidebar1Expanded = false;
        }
    }
}
