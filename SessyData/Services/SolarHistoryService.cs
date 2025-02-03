using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Extensions;
using SessyData.Model;

namespace SessyData.Services
{
    public class SolarHistoryService
    {
        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        public SolarHistoryService(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public void StoreSolarHistoryList(List<SolarHistory> solarHistories)
        {
            try
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var modelContext = scope.ServiceProvider.GetRequiredService<ModelContext>();

                    foreach (var solarHistory in solarHistories)
                    {
                        if (modelContext.SolarHistory.FirstOrDefault(sh => sh.Time == solarHistory.Time) == null)
                        {
                            modelContext.SolarHistory.Add(solarHistory);
                        }
                    }

                    modelContext.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error during store of SolarHistory{ex.ToDetailedString()}");
            }
        }
    }
}
