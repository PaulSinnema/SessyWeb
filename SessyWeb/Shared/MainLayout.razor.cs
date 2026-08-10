using BlazorPro.BlazorSize;
using Microsoft.AspNetCore.Components;
using Radzen;
using SessyData.Model;
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

            _keepMenuExpanded = SettingsService.Current.KeepMenuExpanded;
            SettingsService.SettingsChanged += OnSettingsChanged;

            return base.OnInitializedAsync();
        }

        /// <summary>Whether a menu click leaves the menu open. Mirrors Settings.KeepMenuExpanded.</summary>
        private bool _keepMenuExpanded = true;

        /// <summary>
        /// SettingsService is a singleton, so this runs on a background thread and outside the
        /// circuit — hence InvokeAsync. Unsubscribing in Dispose is what keeps a closed circuit
        /// from being held alive by that singleton.
        /// </summary>
        private void OnSettingsChanged(Settings settings, bool isStartup)
        {
            _keepMenuExpanded = settings.KeepMenuExpanded;

            _ = InvokeAsync(StateHasChanged);
        }

        /// <summary>
        /// Re-renders only when the viewport really changed. Rendering the layout renders @Body
        /// with it — the same reason SetIsBusy routes through the overlay — so a resize used to
        /// cost two full redraws of, on the charging-hours page, a chart of some 17 series over up
        /// to 288 quarters. Twice, because the call was there twice, and even for the sub-pixel
        /// events an iOS toolbar generates, which ScreenInfo.Update ignores anyway.
        /// </summary>
        private async Task OnResizedAsync(BrowserWindowSize browserWindowSize)
        {
            WindowSize = browserWindowSize;

            if (!ScreenInfo.Update(WindowSize.Width, WindowSize.Height))
                return;

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

        /// <summary>
        /// Runs on every menu item click. With KeepMenuExpanded on it does nothing, so the menu
        /// keeps whatever the toggle above it was set to (issue #2); off is the original
        /// behaviour, where each click folded the menu back to icons.
        /// </summary>
        public void CollapseMenu()
        {
            if (_keepMenuExpanded)
                return;

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
            SettingsService.SettingsChanged -= OnSettingsChanged;
        }
    }
}
