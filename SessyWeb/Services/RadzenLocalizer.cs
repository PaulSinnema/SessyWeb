using System.Globalization;
using Radzen;

namespace SessyWeb.Services
{
    /// <summary>
    /// Radzen's built-in texts in the interface language. The Dutch values live in Texts.nl.resx under Radzen's own
    /// resource keys (DataGrid_FilterText, Pager_PageSizeText, ...); null falls back to Radzen's English. The language
    /// comes from LanguageState, not from the culture Radzen passes, so it follows the flags without a reload.
    /// </summary>
    public sealed class RadzenLocalizer(LanguageState language) : ILocalizer
    {
        public string? Get(string key, CultureInfo culture) => language.TranslateOrNull(key);
    }
}
