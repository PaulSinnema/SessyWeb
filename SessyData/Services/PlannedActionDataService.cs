using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class PlannedActionDataService : ServiceBase<PlannedAction>
    {
        public PlannedActionDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory) { }

        /// <summary>
        /// Saves a new plan to the database. Old plans are kept for audit/diagnosis.
        /// Plans older than PlannedAction.MaxPlanRetentionDays are purged automatically.
        /// </summary>
        public async Task SavePlanAsync(IEnumerable<PlannedAction> actions)
        {
            // Purge old plans beyond retention window.
            var cutoff = DateTime.UtcNow.AddDays(-PlannedAction.MaxPlanRetentionDays);

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

            // Find the most recent PlanId.
            var latestPlanId = all.First().PlanId;
            var latestPlan = all.Where(p => p.PlanId == latestPlanId).ToList();

            // Check age.
            var savedAt = latestPlan.Max(p => p.SavedAt);
            var ageHours = (DateTime.UtcNow - savedAt).TotalHours;

            if (ageHours > PlannedAction.MaxPlanAgeHours)
                return new List<PlannedAction>();

            // Only return quarters still relevant (current or future).
            var now = DateTime.UtcNow;
            return latestPlan.Where(p => p.Time >= now.AddMinutes(-15)).ToList();
        }

        /// <summary>
        /// Remove the most recent plan from the database.
        /// </summary>
        public async Task<bool> ClearPlanAsync()
        {
            var all = await GetList(async set =>
                await Task.FromResult(set.OrderByDescending(p => p.SavedAt).ToList()));

            if (!all.Any())
                return false;

            // Find the most recent PlanId.
            var latestPlanId = all.First().PlanId;
            var latestPlan = all.Where(p => p.PlanId == latestPlanId).ToList();

            await Remove(latestPlan, null);

            return true;
        }

        /// <summary>
        /// Returns a summary of all stored plan solves for display on the dashboard.
        /// Returns one entry per PlanId, ordered by SavedAt descending.
        /// </summary>
        public async Task<List<PlanHistoryEntry>> GetPlanHistoryAsync(int maxEntries = 50)
        {
            var all = await GetList(async set =>
                await Task.FromResult(set.OrderByDescending(p => p.SavedAt).ToList()));

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
    }

    /// <summary>
    /// Summary of one MILP solve for display in plan history.
    /// </summary>
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