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
    }
}