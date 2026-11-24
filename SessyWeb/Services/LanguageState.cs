using System.Globalization;
using System.Resources;
using SessyController.Services;

namespace SessyWeb.Services
{
    /// <summary>
    /// Interface language of one circuit. Switching it re-renders the page in the new language without reloading:
    /// texts are looked up with this culture explicitly, not with the thread's culture, which a circuit does not
    /// carry reliably between events.
    /// </summary>
    public sealed class LanguageState
    {
        private static readonly ResourceManager Resources = new("SessyWeb.Resources.Texts", typeof(Texts).Assembly);

        public LanguageState(SettingsService settingsService)
        {
            Code = Texts.Normalize(settingsService.Current?.Language);
        }

        public string Code { get; private set; }

        public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);

        /// <summary>Raised after the language changed, so the layout can re-render.</summary>
        public event Action? Changed;

        /// <summary>The text in this circuit's language; the English text is the key and the fallback.</summary>
        public string Translate(string text)
        {
            if (Code == "en")
                return text;

            try
            {
                return Resources.GetString(text, Culture) ?? text;
            }
            catch (MissingManifestResourceException)
            {
                // No resources built for this language: show the English text rather than break the page.
                return text;
            }
        }

        public void Set(string code)
        {
            code = Texts.Normalize(code);
            if (code == Code)
                return;

            Code = code;
            Changed?.Invoke();
        }
    }
}
