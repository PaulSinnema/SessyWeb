using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class TaxesService : ServiceBase<Taxes>
    {
        public TaxesService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }
    }
}
