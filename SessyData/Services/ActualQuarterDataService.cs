using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services;

public class ActualQuarterDataService : ServiceBase<ActualQuarter>
{
    public ActualQuarterDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

    /// <summary>
    /// Returns all ActualQuarter records in the given time range (inclusive).
    /// Virtual to allow mocking in unit tests.
    /// </summary>
    public virtual async Task<List<ActualQuarter>> GetRangeAsync(DateTime from, DateTime to)
    {
        return await GetList(set => set.Where(item => item.Time >= from && item.Time <= to).ToListAsync());
    }

    /// <summary>
    /// Deletes all ActualQuarter records older than the given cutoff (retention policy: 30 days).
    /// </summary>
    public async Task DeleteOlderThanAsync(DateTime cutoff)
    {
        var toRemove = await GetList(set => set.Where(item => item.Time < cutoff).ToListAsync());

        if (toRemove.Any())
            await Remove(toRemove).ConfigureAwait(false);
    }
}