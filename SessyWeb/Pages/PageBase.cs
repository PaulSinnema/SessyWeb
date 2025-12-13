using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Helpers;

namespace SessyWeb.Pages
{
    public class PageBase : ComponentBase, IDisposable
    {
        [Inject]
        public BatteryContainer? batteryContainer { get; set; }

        [Inject]
        public BatteriesService? _batteriesService { get; set; }

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

        [CascadingParameter]
        public Action<bool> SetIsBusy { get; set; } = default!;

        public bool IsBusy
        {
            set
            {
                if (SetIsBusy != null)
                    SetIsBusy(value);
            }
        }

        public virtual void ScreenInfoChanged(ScreenInfo screenInfo) { }

        public bool IsComponentActive { get; internal set; } = false;

        public bool IsManualOverride => _batteriesService!.IsManualOverride;

        public bool WeAreInControl => _batteriesService!.WeAreInControl;



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

