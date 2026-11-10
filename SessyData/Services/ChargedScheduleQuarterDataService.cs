using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class ChargedScheduleQuarterDataService : ServiceBase<ChargedScheduleQuarter>
    {
        /// <summary>Smaller differences are treated as unchanged.</summary>
        private const double ChangeThresholdW = 0.5;

        public ChargedScheduleQuarterDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        /// <summary>
        /// Stores the quarters whose power differs from the newest stored version (or that are new).
        /// Returns the number of rows written.
        /// </summary>
        public async Task<int> SaveChangedQuartersAsync(IReadOnlyDictionary<DateTime, double> powerByQuarter, DateTime fetchedAt)
        {
            if (powerByQuarter.Count == 0) return 0;

            var first = powerByQuarter.Keys.Min();

            // Bounded: only the quarters of this schedule, each with its handful of versions.
            var stored = await GetList(async set =>
                await Task.FromResult(set.Where(q => q.Time >= first).ToList()));

            var latest = stored
                .GroupBy(q => q.Time)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(q => q.FetchedAt).First().PowerW);

            var changed = powerByQuarter
                .Where(kv => !latest.TryGetValue(kv.Key, out var previous) || Math.Abs(previous - kv.Value) > ChangeThresholdW)
                .Select(kv => new ChargedScheduleQuarter { Time = kv.Key, PowerW = kv.Value, FetchedAt = fetchedAt })
                .ToList();

            if (changed.Count > 0)
                await AddRange(changed);

            return changed.Count;
        }

        /// <summary>Removes all versions of quarters before the cutoff.</summary>
        public async Task RemoveOlderThanAsync(DateTime cutoff) =>
            await RemoveWhere(q => q.Time < cutoff);
    }
}
