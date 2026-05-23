namespace SessyController.Services.Statistics
{
    public enum CheckSeverity { Error, Warning, Info }

    /// <summary>
    /// A single configuration check result shown on the Tips & Checks tab.
    /// </summary>
    public class ConfigurationCheck
    {
        public CheckSeverity Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? ActionUrl { get; set; }
        public string? ActionLabel { get; set; }

        public string SeverityIcon => Severity switch
        {
            CheckSeverity.Error => "🔴",
            CheckSeverity.Warning => "🟡",
            CheckSeverity.Info => "🟢",
            _ => "⚪"
        };
    }
}