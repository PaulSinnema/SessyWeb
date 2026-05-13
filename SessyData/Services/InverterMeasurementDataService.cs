using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class InverterMeasurementDataService : ServiceBase<InverterMeasurement>
    {
        public InverterMeasurementDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory) { }
    }
}