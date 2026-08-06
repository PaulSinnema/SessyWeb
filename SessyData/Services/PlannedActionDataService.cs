using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Services;
using SessyData.Model;

namespace SessyData.Services
{
    public class PlannedActionDataService : ServiceBase<PlannedAction>
    {
        private readonly TimeZoneService _timeZoneService;

        public PlannedActionDataService(
            IServiceScopeFactory serviceScopeFactory,
            TimeZoneService timeZoneService)
            : base(serviceScopeFactory)
        {
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// Saves a new plan to the database. Old plans are kept for audit/diagnosis.
        /// Plans older than PlannedAction.MaxPlanRetentionDays are purged automatically.
        /// </summary>
        public async Task SavePlanAsync(IEnumerable<PlannedAction> actions)
        {
            // Purge old plans beyond retention window — one DELETE, not a fetch plus a query and a
            // delete per row.
            var cutoff = _timeZoneService.Now.AddDays(-PlannedAction.MaxPlanRetentionDays);

            await RemoveWhere(p => p.SavedAt < cutoff).ConfigureAwait(false);

            // Insert new plan — each solve gets a unique PlanId.
            if (actions.Any())
                await Add(actions.ToList()).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads the most recent plan if it is not older than PlannedAction.MaxPlanAgeHours.
        /// Returns an empty list when no valid plan is available.
        /// </summary>
        public async Task<List<PlannedAction>> LoadPlanAsync()
        {
            // Only the newest plan matters, so ask SQL for its identity instead of sorting the
            // whole table in memory.
            var latest = await Query(async set => await set
                .OrderByDescending(p => p.SavedAt)
                .Select(p => new { p.PlanId, p.SavedAt })
                .FirstOrDefaultAsync()).ConfigureAwait(false);

            if (latest == null)
                return new List<PlannedAction>();

            if ((_timeZoneService.Now - latest.SavedAt).TotalHours > PlannedAction.MaxPlanAgeHours)
                return new List<PlannedAction>();

            var from = _timeZoneService.Now.AddMinutes(-15);

            return await GetList(async set => await set
                .Where(p => p.PlanId == latest.PlanId && p.Time >= from)
                .ToListAsync()).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns a summary of all stored plan solves for display on the dashboard.
        /// </summary>
        /// <param name="maxEntries">Maximum number of plans to return, most recent first.</param>
        /// <param name="since">When set, only plans saved at or after this time are included.</param>
        /// <param name="until">When set, only plans saved strictly before this time are included.</param>
        public async Task<List<PlanHistoryEntry>> GetPlanHistoryAsync(
            int maxEntries = 50, DateTime? since = null, DateTime? until = null)
        {
            // Grouped in SQL. This used to materialize every row of the table — 80.000 of them on
            // the production database — on every dashboard refresh, per open browser tab.
            // Reason and ObjectiveEur are identical for all rows of one plan, so Min() picks the
            // value without needing a correlated subquery.
            return await Query(async set =>
            {
                var query = set;

                if (since != null)
                    query = query.Where(p => p.SavedAt >= since.Value);

                if (until != null)
                    query = query.Where(p => p.SavedAt < until.Value);

                return await query
                    .GroupBy(p => p.PlanId)
                    .Select(g => new PlanHistoryEntry
                    {
                        PlanId = g.Key,
                        SavedAt = g.Max(p => p.SavedAt),
                        Reason = g.Min(p => p.Reason) ?? string.Empty,
                        ObjectiveEur = g.Min(p => p.ObjectiveEur),
                        QuarterCount = g.Count(),
                        PlanHorizon = g.Max(p => p.Time),
                    })
                    .OrderByDescending(e => e.SavedAt)
                    .Take(maxEntries)
                    .ToListAsync();
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads every quarter of one specific historical plan, ordered by time.
        /// Used to overlay a past plan on the chart for comparison against what actually happened.
        /// </summary>
        public async Task<List<PlannedAction>> GetPlanAsync(Guid planId)
        {
            return await GetList(async set =>
                await Task.FromResult(
                    set.Where(p => p.PlanId == planId)
                       .OrderBy(p => p.Time)
                       .ToList()));
        }

        /// <summary>Removes all saved plan entries.</summary>
        public async Task ClearPlanAsync()
        {
            await RemoveWhere(p => true).ConfigureAwait(false);
        }
    }

    public class PlanHistoryEntry
    {
        public Guid PlanId { get; set; }
        public DateTime SavedAt { get; set; }
        public string Reason { get; set; } = string.Empty;
        public double ObjectiveEur { get; set; }
        public int QuarterCount { get; set; }
        public DateTime PlanHorizon { get; set; }
    }
}