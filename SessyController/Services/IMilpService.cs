using SessyController.Services.Statistics;

namespace SessyController.Services
{
    public interface IMilpService
    {
        Task<PlanStatistics> GetPlanStatisticsAsync(DateTime now);
        Task ClearPlanAsync();
    }
}