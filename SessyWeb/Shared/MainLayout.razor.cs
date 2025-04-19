using Microsoft.AspNetCore.Mvc;
using Radzen;
using System.Data;

namespace SessyWeb.Shared
{
    public partial class MainLayout
    {
        public static string NewTheme { get; set; } = "Dark";

        public MenuItemDisplayStyle DisplayStyle { get; set; } = MenuItemDisplayStyle.Icon;

        private const string MenuStyleIcon = "width: 100%; min-width: 50px;";
        private const string MenuStyleIconAndText = "width: 100%; min-width: 200px;";

        public string? MenuStyle { get; set; }

        public bool IsMobile = false;

        protected override void OnInitialized()
        {
            MenuStyle = MenuStyleIcon;

            base.OnInitialized();
        }

        void ChangeTheme(string theme)
        {
            NewTheme = theme;
            ThemeService.SetTheme(theme, true);
        }

        void ToggleDisplayStyle()
        {
            if (DisplayStyle == MenuItemDisplayStyle.Icon)
            {
                MenuIconAndText();
            }
            else
            {
                MenuIcon();
            }
        }

        public void CollapseMenu()
        {
            MenuIcon();
        }
        private void MenuIcon()
        {
            DisplayStyle = MenuItemDisplayStyle.Icon;
            MenuStyle = MenuStyleIcon;
        }

        private void MenuIconAndText()
        {
            DisplayStyle = MenuItemDisplayStyle.IconAndText;
            MenuStyle = MenuStyleIconAndText;
        }

        public void OnChange(bool isMobile)
        {
            IsMobile = isMobile;
            StateHasChanged();
        }
    }
}
