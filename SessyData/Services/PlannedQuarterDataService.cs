using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services;

public class PlannedQuarterDataService : ServiceBase<PlannedQuarter>
{
    public PlannedQuarterDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

    /// <summary>
    /// Upserts a list of PlannedQuarter records based on Time (unique key).
    /// </summary>
    public async Task SaveAsync(List<PlannedQuarter> quarters)
    {
        await AddOrUpdate(quarters, (item, set) => set.FirstOrDefault(q => q.Time == item.Time));
    }

    /// <summary>
    /// Returns all PlannedQuarter records in the given time range (inclusive).
    /// Virtual to allow mocking in unit tests.
    /// </summary>
    public virtual async Task<List<PlannedQuarter>> GetRangeAsync(DateTime from, DateTime to)
    {
        return await GetList(set => set.Where(item => item.Time >= from && item.Time <= to).ToListAsync());
    }

    /// <summary>
    /// Returns the PlannedQuarter for the exact quarter timestamp, or null if not found.
    /// Virtual to allow mocking in unit tests.
    /// </summary>
    public virtual async Task<PlannedQuarter?> GetForQuarterAsync(DateTime quarter)
    {
        return await Get(async set => await set.Where(item => item.Time == quarter).SingleOrDefaultAsync());
    }

    /// <summary>
    /// Deletes all PlannedQuarter records older than the given cutoff.
    /// </summary>
    public async Task DeleteOlderThanAsync(DateTime cutoff)
    {
        var toRemove = await GetList(set => set.Where(item => item.Time < cutoff).ToListAsync());

        if (toRemove.Any())
            await Remove(toRemove).ConfigureAwait(false);
    }
}