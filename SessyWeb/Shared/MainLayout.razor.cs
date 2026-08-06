using BlazorPro.BlazorSize;
using Microsoft.AspNetCore.Components;
using Radzen;
using SessyWeb.Components;
using SessyWeb.Helpers;

namespace SessyWeb.Shared
{
    public partial class MainLayout
    {
        [Inject] 
        private IResizeListener ResizeListener { get; set; } = default!;

        public BrowserWindowSize? WindowSize { get; private set; }

        public static string NewTheme { get; set; } = "Dark Software";

        public MenuItemDisplayStyle DisplayStyle { get; set; } = MenuItemDisplayStyle.Icon;

        private const string MenuStyleIcon = "width: 100%; min-width: 50px; height: 100%;";
        private const string MenuStyleIconAndText = "width: 100%; min-width: 200px; height: 100%;";

        public ScreenInfo ScreenInfo { get; set; } = new();

        private int screenWidth { get; set; }
        private int screenHeight { get; set; }

        public string? MenuStyle { get; set; }

        private BusyOverlay? _busyOverlay;

        /// <summary>
        /// Routes the busy flag to the overlay component, which redraws only itself. Calling
        /// StateHasChanged on the layout re-rendered @Body — on the charging-hours page that is a
        /// chart of some 17 series over up to 288 quarters, twice per data refresh.
        /// The ref is only set after the first render, so a very early call is simply ignored.
        /// </summary>
        public void SetIsBusy(bool isBusy) => _busyOverlay?.SetBusy(isBusy);

        protected override void OnInitialized()
        {
            MenuStyle = MenuStyleIcon;

            base.OnInitialized();
        }

        protected override Task OnInitializedAsync()
        {
            ResizeListener.OnResized += OnResized;

            return base.OnInitializedAsync();
        }

        private async Task OnResizedAsync(BrowserWindowSize browserWindowSize)
        {
            WindowSize = browserWindowSize;

            ScreenInfo.Update(WindowSize.Width, WindowSize.Height);

            Console.WriteLine($"ScreenInfo: {ScreenInfo}");

            await InvokeAsync(StateHasChanged);
            
            await InvokeAsync(StateHasChanged);
        }

        private void OnResized(object? sender, BrowserWindowSize browserWindowSize)
        {
            _ = OnResizedAsync(browserWindowSize);
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

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            
            ResizeListener.OnResized -= OnResized;
        }
    }
}
