namespace SessyWeb.Helpers
{
    /// <summary>
    /// Colours for markup built in C#. Values point at the palette tokens in wwwroot/css/site.css,
    /// so a colour changes in one place for the whole GUI.
    /// </summary>
    public static class SessyColors
    {
        /// <summary>Badge style per battery mode — same colours as the charging hours chart.</summary>
        public static string? ModeBadgeStyle(string? mode) => mode switch
        {
            "Charging" => Filled("--sessy-charge"),
            "Discharging" => Filled("--sessy-discharge"),
            "SolarOnly" or "Solar only" => Filled("--sessy-solar-only"),
            "ZeroNetHome" or "Zero net home" => Filled("--sessy-znh"),
            _ => null
        };

        /// <summary>Text colour per battery mode, readable on light and dark themes.</summary>
        public static string ModeTextColor(string? mode) => mode switch
        {
            "Charging" => "var(--sessy-charge-text)",
            "Discharging" => "var(--sessy-discharge-text)",
            _ => "var(--rz-text-color)"
        };

        // Bright fill, always dark text.
        private static string Filled(string token) => $"background-color: var({token}); color: var(--sessy-on-bright);";
    }
}
