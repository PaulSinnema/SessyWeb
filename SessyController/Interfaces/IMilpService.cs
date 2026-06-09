using SessyController.Services.Items;
using SessyController.Services.Statistics;

namespace SessyController.Interfaces
{
    public interface IMilpService : IDisposable
    {
        Task BuildPlanAsync(List<QuarterlyInfo> quarterlyInfos, double currentSocWh);
        Task<(ChargingModes.Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter);
        Task<PlanStatistics> GetPlanStatisticsAsync(DateTime now, double currentSocWh);
        Task ClearPlanAsync();
        Task<bool> TryRestorePlanAsync();
        bool HasPlanFor(DateTime quarter);
    }
}