using Microsoft.AspNetCore.Components;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Helpers;
using Microsoft.Extensions.Logging;

namespace SessyWeb.Components
{
    public class BaseComponent : ComponentBase
    {
        [Inject]
        public TimeZoneService? _timeZoneService { get; set; }

        [Inject]
        public BatteryContainer? batteryContainer { get; set; }

        [Inject]
        public BatteriesService? _batteriesService { get; set; }

        private ScreenInfo? _screenInfo;

        [CascadingParameter(Name = "ScreenInfo")]
        public ScreenInfo? ScreenInfo
        {
            get => _screenInfo;
            set
            {
                _screenInfo = value;

                if (_screenInfo != null)
                    ScreenInfoChanged(_screenInfo);
            }
        }

        /// <summary>Called when the cascading ScreenInfo arrives or changes.</summary>
        public virtual void ScreenInfoChanged(ScreenInfo screenInfo) { }

        public bool IsManualOverride => _batteriesService!.IsManualOverride;

        public bool WeAreInControl => _batteriesService!.WeAreInControl;

        public bool ChargedInControl => _batteriesService!.ChargedInControl;



        /// <summary>True while WhileLoadingAsync runs; starts true so a page shows its spinner before the first load.</summary>
        protected bool Loading { get; private set; } = true;

        [Inject]
        private ILoggerFactory? LoggerFactory { get; set; }

        /// <summary>
        /// Runs the work with Loading set. A failing load is logged and leaves the card empty: an
        /// exception escaping a lifecycle method would take the whole circuit down with it, so one
        /// card that cannot load (or is torn down by a tab switch halfway) no longer breaks the page.
        /// </summary>
        protected async Task WhileLoadingAsync(Func<Task> work)
        {
            Loading = true;

            try
            {
                await work().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LoggerFactory?.CreateLogger(GetType()).LogError(ex, "{Component}: loading failed", GetType().Name);
            }
            finally
            {
                Loading = false;
            }
        }

        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}
