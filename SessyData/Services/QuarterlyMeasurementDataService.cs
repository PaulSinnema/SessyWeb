using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class QuarterlyMeasurementDataService : ServiceBase<QuarterlyMeasurement>
    {
        public QuarterlyMeasurementDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory) { }
    }
}