namespace SessyCommon.Configurations
{
    /// <summary>
    /// Infrastructure-only settings that must be available before the database
    /// is accessible. All EMS settings live in the Settings DB table instead.
    /// </summary>
    public class SettingsConfig
    {
        /// <summary>The timezone used for bootstrap before the DB is loaded.</summary>
        public string? Timezone { get; set; }

        /// <summary>Directory path for automated database backups.</summary>
        public string? DatabaseBackupDirectory { get; set; }
    }
}
