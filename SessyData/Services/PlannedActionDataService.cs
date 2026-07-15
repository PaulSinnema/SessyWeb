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
            // Purge old plans beyond retention window.
            var cutoff = _timeZoneService.Now.AddDays(-PlannedAction.MaxPlanRetentionDays);

            var old = await GetList(async set =>
                await Task.FromResult(
                    set.Where(p => p.SavedAt < cutoff).ToList()));

            if (old.Any())
                await Remove(old, (item, set) =>
                    set.FirstOrDefault(p => p.Id == item.Id));

            // Insert new plan — each solve gets a unique PlanId.
            if (actions.Any())
                await Add(actions.ToList());
        }

        /// <summary>
        /// Loads the most recent plan if it is not older than PlannedAction.MaxPlanAgeHours.
        /// Returns an empty list when no valid plan is available.
        /// </summary>
        public async Task<List<PlannedAction>> LoadPlanAsync()
        {
            var all = await GetList(async set =>
                await Task.FromResult(set.OrderByDescending(p => p.SavedAt).ToList()));

            if (!all.Any())
                return new List<PlannedAction>();

            var latestPlanId = all.First().PlanId;
            var latestPlan = all.Where(p => p.PlanId == latestPlanId).ToList();

            var savedAt = latestPlan.Max(p => p.SavedAt);
            var ageHours = (_timeZoneService.Now - savedAt).TotalHours;

            if (ageHours > PlannedAction.MaxPlanAgeHours)
                return new List<PlannedAction>();

            var now = _timeZoneService.Now;
            return latestPlan.Where(p => p.Time >= now.AddMinutes(-15)).ToList();
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
            var all = await GetList(async set =>
                await Task.FromResult(set.OrderByDescending(p => p.SavedAt).ToList()));

            if (since != null)
                all = all.Where(p => p.SavedAt >= since.Value).ToList();

            if (until != null)
                all = all.Where(p => p.SavedAt < until.Value).ToList();

            return all
                .GroupBy(p => p.PlanId)
                .Select(g => new PlanHistoryEntry
                {
                    PlanId = g.Key,
                    SavedAt = g.Max(p => p.SavedAt),
                    Reason = g.First().Reason,
                    ObjectiveEur = g.First().ObjectiveEur,
                    QuarterCount = g.Count(),
                    PlanHorizon = g.Max(p => p.Time),
                })
                .OrderByDescending(e => e.SavedAt)
                .Take(maxEntries)
                .ToList();
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
            var existing = await GetList(async set =>
                await Task.FromResult(set.ToList()));

            if (existing.Any())
                await Remove(existing, (item, set) =>
                    set.FirstOrDefault(p => p.Id == item.Id));
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