using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class AppVersionDataService : ServiceBase<AppVersion>
    {
        public AppVersionDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        /// <summary>
        /// Stamps this startup: inserts the version on first sight, otherwise refreshes LastSeen
        /// and the migration it ran against. FirstSeen carries [SkipCopy], so the update leaves it
        /// alone. Returns the version that ran before this one, or null when this database has
        /// never been started before.
        /// </summary>
        public async Task<AppVersion?> RecordStartupAsync(string version, string lastMigration, DateTime now)
        {
            var previous = await Get(async set =>
                await Task.FromResult(set.OrderByDescending(v => v.LastSeen).FirstOrDefault()));

            await AddOrUpdate(
                new List<AppVersion>
                {
                    new AppVersion
                    {
                        Version = version,
                        FirstSeen = now,
                        LastSeen = now,
                        LastMigration = lastMigration
                    }
                },
                (item, set) => set.FirstOrDefault(v => v.Version == item.Version));

            return previous;
        }
    }
}
