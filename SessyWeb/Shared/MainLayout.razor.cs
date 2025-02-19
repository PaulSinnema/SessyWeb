using Microsoft.AspNetCore.Mvc;
using Radzen;

namespace SessyWeb.Shared
{
    public partial class MainLayout
    {
        public static string NewTheme { get; set; } = "Dark";

        public MenuItemDisplayStyle DisplayStyle { get; set; } = MenuItemDisplayStyle.Icon;

        private const string MenuStyleIcon = "width: 100%; min-width: 50px;";
        private const string MenuStyleIconAndText = "width: 100%; min-width: 200px;";

        public string MenuStyle { get; set; }

        protected override void OnInitialized()
        {
            MenuStyle = MenuStyleIcon;

            base.OnInitialized();
        }

        [IgnoreAntiforgeryToken]
        void ChangeTheme(string theme)
        {
            NewTheme = theme;
            ThemeService.SetTheme(theme, true);
        }

        void ToggleDisplayStyle()
        {
            if (DisplayStyle == MenuItemDisplayStyle.Icon)
            {
                DisplayStyle = MenuItemDisplayStyle.IconAndText;
                MenuStyle = MenuStyleIconAndText;
            }
            else
            {
                DisplayStyle = MenuItemDisplayStyle.Icon;
                MenuStyle = MenuStyleIcon;
            }
        }

        public void CollapseMenu()
        {
            DisplayStyle = MenuItemDisplayStyle.Icon;
        }
    }
}
