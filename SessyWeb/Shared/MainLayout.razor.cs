using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc;
using Radzen;
using SessyWeb.Services;
using System.Data;

namespace SessyWeb.Shared
{
    public partial class MainLayout
    {
        [Inject]
        public ScreenSizeService? _screenSizeService { get; set; }

        public static string NewTheme { get; set; } = "Dark";

        public MenuItemDisplayStyle DisplayStyle { get; set; } = MenuItemDisplayStyle.Icon;

        private const string MenuStyleIcon = "width: 100%; min-width: 50px;";
        private const string MenuStyleIconAndText = "width: 100%; min-width: 200px;";

        private int screenWidth { get; set; }
        private int screenHeight { get; set; }

        public string? MenuStyle { get; set; }

        public bool IsMobile { get; set; } = false;

        protected override void OnInitialized()
        {
            MenuStyle = MenuStyleIcon;

            base.OnInitialized();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                _screenSizeService!.OnScreenSizeChanged += _screenSizeService_OnScreenSizeChanged;

                screenHeight = await _screenSizeService.GetScreenHeightAsync();
                screenWidth = await _screenSizeService.GetScreenWidthAsync();

                _screenSizeService_OnScreenSizeChanged(screenHeight, screenWidth);
            }


            await base.OnAfterRenderAsync(firstRender);
        }

        private void _screenSizeService_OnScreenSizeChanged(int height, int width)
        {
            screenWidth = width;
            screenHeight = height;

            IsMobile = width <= 700;

            StateHasChanged();
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
    }
}
