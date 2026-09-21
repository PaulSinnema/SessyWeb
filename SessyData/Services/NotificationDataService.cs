using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    /// <summary>Data access for the generic notification queue.</summary>
    public class NotificationDataService : ServiceBase<Notification>
    {
        public NotificationDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory)
        {
        }

        /// <summary>Newest first, capped so the tab never pulls the whole table.</summary>
        public async Task<List<Notification>> GetRecentAsync(int max = 100)
        {
            return await GetList(async set =>
                await set.OrderByDescending(n => n.CreatedAt).Take(max).ToListAsync());
        }

        public async Task<int> UnreadCountAsync()
        {
            return await Query(async set => await set.CountAsync(n => !n.IsRead));
        }

        public async Task<int> UnreadCountAsync(NotificationSeverity severity)
        {
            return await Query(async set => await set.CountAsync(n => !n.IsRead && n.Severity == severity));
        }

        /// <summary>Deletes every notification with this de-dup key (used to clear on recovery).
        /// Returns how many rows were removed.</summary>
        public async Task<int> ClearByKeyAsync(string dedupKey)
        {
            int removed = 0;

            await _dbHelper.ExecuteWriteAsync(async db =>
            {
                removed = await db.Set<Notification>()
                    .Where(n => n.DedupKey == dedupKey)
                    .ExecuteDeleteAsync();
            });

            return removed;
        }

        /// <summary>The newest notification with this de-dup key AND identical message (read or
        /// not), or null. Matching on the message too means a different failure reason under the
        /// same key becomes a separate notification instead of collapsing into one.</summary>
        public async Task<Notification?> FindByKeyAndMessageAsync(string dedupKey, string message)
        {
            return await Get(async set =>
                await set.Where(n => n.DedupKey == dedupKey && n.Message == message)
                         .OrderByDescending(n => n.CreatedAt)
                         .FirstOrDefaultAsync());
        }

        public async Task AddAsync(Notification notification)
        {
            await AddRange(new List<Notification> { notification });
        }

        /// <summary>A repeat: bump the timestamp and message, increment the occurrence counter and
        /// mark it unread again, instead of adding a duplicate row.</summary>
        public async Task IncrementAsync(int id, DateTime createdAt, string message)
        {
            await _dbHelper.ExecuteWriteAsync(async db =>
            {
                await db.Set<Notification>()
                    .Where(n => n.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(n => n.CreatedAt, createdAt)
                        .SetProperty(n => n.Message, message)
                        .SetProperty(n => n.IsRead, false)
                        .SetProperty(n => n.Count, n => n.Count + 1));
            });
        }

        public async Task MarkAllReadAsync()
        {
            await _dbHelper.ExecuteWriteAsync(async db =>
            {
                await db.Set<Notification>()
                    .Where(n => !n.IsRead)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
            });
        }

        public async Task DeleteAsync(int id)
        {
            await RemoveWhere(n => n.Id == id);
        }

        public async Task DeleteAllAsync()
        {
            await RemoveWhere(n => true);
        }
    }
}
