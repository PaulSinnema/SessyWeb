namespace SessyCommon.Configurations
{
    /// <summary>
    /// Infrastructure-only settings that must be available before the database
    /// is accessible. All EMS settings live in the Settings DB table instead.
    ///
    /// Timezone used to live here as well. It now comes from the Settings row, which startup reads
    /// directly (SqliteSetup.TryReadTimeZone) before anything writes a timestamp, so there is no
    /// second place where it can be set to something else.
    /// </summary>
    public class SettingsConfig
    {
        /// <summary>Directory path for automated database backups.</summary>
        public string? DatabaseBackupDirectory { get; set; }
    }
}
