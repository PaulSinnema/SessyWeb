using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Generic, application-wide notification queue. Any service can raise a notification; the
    /// Settings "Notifications" tab shows them and the Settings menu carries a red dot while unread
    /// entries exist. Persisted through <see cref="NotificationDataService"/> so nothing is lost on
    /// a restart.
    /// </summary>
    public class NotificationService
    {
        private readonly NotificationDataService _dataService;
        private readonly TimeZoneService _timeZoneService;

        public NotificationService(NotificationDataService dataService, TimeZoneService timeZoneService)
        {
            _dataService = dataService;
            _timeZoneService = timeZoneService;
        }

        /// <summary>Raised after any change so the menu badge and open tabs can refresh.</summary>
        public event Action? Changed;

        /// <summary>Cached so the sidebar badge does not query the database on every render.</summary>
        public int UnreadCount { get; private set; }

        /// <summary>Unread counts split by severity, so the badge can show red only for real errors.</summary>
        public int UnreadErrorCount { get; private set; }
        public int UnreadWarningCount { get; private set; }

        /// <summary>
        /// Raises a notification. When <paramref name="dedupKey"/> is set and an unread entry with
        /// that key already exists, it is refreshed instead of adding a new row — so a repeating
        /// failure does not flood the queue.
        /// </summary>
        public async Task AddAsync(NotificationSeverity severity, string category, string title,
                                   string message, string? dedupKey = null)
        {
            var now = _timeZoneService.Now;

            // Always de-duplicate: without an explicit key, category+title identifies a repeat.
            var key = dedupKey ?? $"{category}|{title}";

            // Identity is key + message: a repeat with the same reason increments; a different
            // reason under the same key is kept as its own notification.
            var existing = await _dataService.FindByKeyAndMessageAsync(key, message);

            if (existing != null)
            {
                await _dataService.IncrementAsync(existing.Id, now, message);
                await RefreshUnreadAsync();
                return;
            }

            await _dataService.AddAsync(new Notification
            {
                CreatedAt = now,
                Severity = severity,
                Category = category,
                Title = title,
                Message = message,
                IsRead = false,
                DedupKey = key
            });

            await RefreshUnreadAsync();
        }

        public async Task<List<Notification>> GetRecentAsync(int max = 100)
        {
            return await _dataService.GetRecentAsync(max);
        }

        public async Task MarkAllReadAsync()
        {
            await _dataService.MarkAllReadAsync();
            await RefreshUnreadAsync();
        }

        public async Task DeleteAsync(int id)
        {
            await _dataService.DeleteAsync(id);
            await RefreshUnreadAsync();
        }

        public async Task DeleteAllAsync()
        {
            await _dataService.DeleteAllAsync();
            await RefreshUnreadAsync();
        }

        /// <summary>Removes every notification with this de-dup key. Used to clear a failure
        /// notification once the operation succeeds again.</summary>
        public async Task ClearByKeyAsync(string dedupKey)
        {
            var removed = await _dataService.ClearByKeyAsync(dedupKey);

            if (removed > 0)
                await RefreshUnreadAsync();
        }

        /// <summary>Recomputes the cached unread counts and notifies listeners.</summary>
        public async Task RefreshUnreadAsync()
        {
            UnreadCount = await _dataService.UnreadCountAsync();
            UnreadErrorCount = await _dataService.UnreadCountAsync(NotificationSeverity.Error);
            UnreadWarningCount = await _dataService.UnreadCountAsync(NotificationSeverity.Warning);
            Changed?.Invoke();
        }
    }
}
