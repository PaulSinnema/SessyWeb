using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;
using SessyWeb.Helpers;

namespace SessyWeb.Pages
{
    public class PageBase : ComponentBase, IDisposable
    {
        [Inject]
        public BatteryContainer? batteryContainer { get; set; }

#if DEBUG
        public bool HideId = false;
#else
        public bool HideId = true;
#endif

        private ScreenInfo? _screenInfo;

        [CascadingParameter(Name = "ScreenInfo")]
        public ScreenInfo? ScreenInfo
        {
            get => _screenInfo;
            set
            {
                _screenInfo = value;

                if (_screenInfo != null)
                {
                    ScreenInfoChanged(_screenInfo);
                }
            }
        }

        public virtual void ScreenInfoChanged(ScreenInfo screenInfo) { }

        public bool IsComponentActive { get; internal set; } = false;



        protected override void OnInitialized()
        {
            IsComponentActive = true;
        }

        private bool _isDisposed = false;

        public virtual void Dispose()
        {
            if (!_isDisposed)
            {
                IsComponentActive = false;

                _isDisposed = true;
            }
        }

        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}

