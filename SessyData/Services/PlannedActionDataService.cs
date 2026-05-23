using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class PlannedActionDataService : ServiceBase<PlannedAction>
    {
        public PlannedActionDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory) { }

        /// <summary>
        /// Saves the entire plan (all quarters) atomically.
        /// Deletes the previous plan first, then inserts the new one.
        /// </summary>
        public async Task SavePlanAsync(IEnumerable<PlannedAction> actions)
        {
            // Delete all existing planned actions.
            var existing = await GetList(async set =>
                await Task.FromResult(set.ToList()));

            if (existing.Any())
                await Remove(existing, (item, set) =>
                    set.FirstOrDefault(p => p.Id == item.Id));

            // Insert new plan.
            if (actions.Any())
                await Add(actions.ToList());
        }

        /// <summary>
        /// Loads the saved plan if it is not older than PlannedAction.MaxPlanAgeHours.
        /// Returns an empty list when no valid plan is available.
        /// </summary>
        public async Task<List<PlannedAction>> LoadPlanAsync()
        {
            var all = await GetList(async set =>
                await Task.FromResult(set.OrderBy(p => p.Time).ToList()));

            if (!all.Any())
                return new List<PlannedAction>();

            // Check age of the plan.
            var savedAt = all.Max(p => p.SavedAt);
            var ageHours = (DateTime.UtcNow - savedAt).TotalHours;

            if (ageHours > PlannedAction.MaxPlanAgeHours)
                return new List<PlannedAction>();

            // Only return quarters that are still in the future.
            var now = DateTime.UtcNow;
            return all.Where(p => p.Time >= now.AddMinutes(-15)).ToList();
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
}