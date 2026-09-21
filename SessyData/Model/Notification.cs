using System.ComponentModel.DataAnnotations;

namespace SessyData.Model
{
    /// <summary>Severity of a user-facing notification. Mirrors the Tips &amp; Checks levels.</summary>
    public enum NotificationSeverity
    {
        Information = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>
    /// A single entry in the generic notification queue. Persisted so a problem raised while nobody
    /// was watching (a failed nightly backup, a planner hang, ...) survives a restart and stays
    /// visible on the Notifications tab until the user deletes it.
    /// </summary>
    public class Notification
    {
        [Key]
        public int Id { get; set; }

        /// <summary>When the notification was raised (application timezone).</summary>
        public DateTime CreatedAt { get; set; }

        public NotificationSeverity Severity { get; set; }

        /// <summary>Free-form group, e.g. "Backup" or "Planner". Used for display and de-duplication.</summary>
        public string Category { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        /// <summary>How many times this notification has occurred. Repeats increment this instead
        /// of adding a duplicate row, so a recurring failure cannot flood the queue.</summary>
        public int Count { get; set; } = 1;

        /// <summary>Unread entries drive the red dot on the Settings menu and the Notifications tab.</summary>
        public bool IsRead { get; set; }

        /// <summary>
        /// Optional de-duplication key. When set, raising a notification whose key already has an
        /// unread entry refreshes that entry instead of adding another — so a repeating failure
        /// (e.g. a nightly backup that keeps failing) does not flood the queue.
        /// </summary>
        public string? DedupKey { get; set; }
    }
}
