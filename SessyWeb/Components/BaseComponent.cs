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

        [Inject]
        private SessyWeb.Services.LanguageState? LanguageState { get; set; }

        /// <summary>Cascaded by MainLayout; a change re-renders every component in the new language.</summary>
        [CascadingParameter(Name = "Language")]
        public string? LanguageCode { get; set; }

        /// <summary>Culture of the interface language, for day and month names.</summary>
        protected System.Globalization.CultureInfo UiCulture => LanguageState?.Culture ?? System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>The text in the chosen language; the English text is the key.</summary>
        protected string T(string text) => LanguageState?.Translate(text) ?? text;

        /// <summary>The translated format string filled in with Dutch number and date formatting.</summary>
        protected string T(string format, params object?[] args) => string.Format(GetFormatProvider(), T(format), args);

        /// <summary>A battery mode ("HoldReserve", "Hold reserve", "Charging", ...) in the interface language.</summary>
        protected string ModeText(string? mode) => (mode ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant() switch
        {
            "" => string.Empty,
            "charging" => T("Charging"),
            "discharging" => T("Discharging"),
            "zeronethome" => T("Zero Net Home"),
            "holdreserve" => T("Hold reserve"),
            "disabled" => T("Disabled"),
            _ => T(mode!)
        };

        /// <summary>What decides a battery's end of life, in the interface language.</summary>
        protected string LimitText(SessyController.Services.Statistics.LifeLimit limit) =>
            T(limit == SessyController.Services.Statistics.LifeLimit.Capacity ? "Fade" : limit.ToString()).ToLowerInvariant();

        /// <summary>Translated text that may hold markup such as &lt;b&gt;; the arguments are HTML-encoded.</summary>
        protected MarkupString TM(string format, params object?[] args) =>
            new(string.Format(GetFormatProvider(), T(format), args.Select(a => (object?)System.Net.WebUtility.HtmlEncode(Convert.ToString(a, GetFormatProvider()))).ToArray()));
    }
}
