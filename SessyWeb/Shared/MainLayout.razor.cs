using Microsoft.AspNetCore.Mvc;
using Radzen;

namespace SessyWeb.Shared
{
    public partial class MainLayout
    {
        public static string NewTheme { get; set; } = "Dark";

        public MenuItemDisplayStyle DisplayStyle { get; set; } = MenuItemDisplayStyle.Icon;

        [IgnoreAntiforgeryToken]
        void ChangeTheme(string theme)
        {
            NewTheme = theme;
            ThemeService.SetTheme(theme, true);
        }

        void ToggleDisplayStyle()
        {
            if (DisplayStyle == MenuItemDisplayStyle.Icon)
                DisplayStyle = MenuItemDisplayStyle.IconAndText;
            else
                DisplayStyle = MenuItemDisplayStyle.Icon;
        }

        public void CollapseMenu()
        {
            DisplayStyle = MenuItemDisplayStyle.Icon;
        }
    }
}
