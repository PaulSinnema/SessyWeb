using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    /// <summary>Data access service for the Settings table.</summary>
    public class SettingsDataService : ServiceBase<Settings>
    {
        public SettingsDataService(IServiceScopeFactory serviceScopeFactory)
            : base(serviceScopeFactory)
        {
        }
    }
}
