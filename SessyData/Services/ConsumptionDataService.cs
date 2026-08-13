using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class ConsumptionDataService : ServiceBase<Consumption>
    {
        public ConsumptionDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        // Removed: GetConsumptionBetween. It had no callers and summed ConsumptionWh raw, which is
        // Watts per quarter — the conversion now lives in one place, MeasurementView.ConsumptionKWh.
        // Read consumption through QuarterlyFactsService.
    }
}
