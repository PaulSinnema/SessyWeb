using SessyController.Services.Items;
using SessyController.Services.Statistics;

namespace SessyController.Interfaces
{
    public interface IMilpService
    {
        Task<(ChargingModes.Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter);
        Task<PlanStatistics> GetPlanStatisticsAsync(DateTime now, double currentSocWh);
        Task ClearPlanAsync();
    }
}