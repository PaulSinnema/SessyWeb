namespace SessyWeb
{
    /// <summary>
    /// Marker for the shared user-interface texts. The English text is the key; Resources/Texts.nl.resx holds the
    /// Dutch. A text without a translation shows in English.
    /// </summary>
    public class Texts
    {
        /// <summary>A language the interface offers: code, name in its own language and the flag in wwwroot/Images/flags.</summary>
        public sealed record Language(string Code, string Name, string Flag);

        /// <summary>Records, not tuples: Radzen binds to properties, and tuple members are fields.</summary>
        public static readonly IReadOnlyList<Language> Languages = new[]
        {
            new Language("nl", "Nederlands", "/Images/flags/nl.svg"),
            new Language("en", "English", "/Images/flags/gb.svg")
        };

        public static string Normalize(string? code) => Languages.Any(l => l.Code == code) ? code! : "en";
    }
}
