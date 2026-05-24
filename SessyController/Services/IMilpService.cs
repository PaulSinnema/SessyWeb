using SessyController.Services.Items;
using SessyController.Services.Statistics;

namespace SessyController.Services
{
    public interface IMilpService
    {
        Task<(ChargingModes.Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter);
        Task<PlanStatistics> GetPlanStatisticsAsync(DateTime now, double currentSocWh);
        Task ClearPlanAsync();
    }
}