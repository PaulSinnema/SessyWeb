using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class BatteryQuarterMeasurementDataService : ServiceBase<BatteryQuarterMeasurement>
    {
        public BatteryQuarterMeasurementDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory) { }
    }
}
