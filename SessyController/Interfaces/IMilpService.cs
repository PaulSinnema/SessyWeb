using SessyController.Services.Items;
using SessyController.Services.Statistics;
using SessyCommon.Enums;

namespace SessyController.Interfaces
{
    public interface IMilpService : IDisposable
    {
        Task BuildPlanAsync(List<QuarterlyInfo> quarterlyInfos, double currentSocWh);
        Task<(Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter);
        Task<PlanStatistics> GetPlanStatisticsAsync(DateTime now, double currentSocWh);
        Task ClearPlanAsync();
        Task<bool> TryRestorePlanAsync();
        bool HasPlanFor(DateTime quarter);

        /// <summary>
        /// Makes the next BuildPlanAsync rebuild unconditionally, instead of only on a price
        /// change, SOC deviation or settings change. The reason is stored with the plan.
        /// </summary>
        void ForceRebuild(string reason);
    }
}