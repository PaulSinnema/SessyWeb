using SessyController.Services.Statistics;

namespace SessyController.Services
{
    public interface IMilpService
    {
        PlanStatistics GetPlanStatistics(DateTime now);
    }
}