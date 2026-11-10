using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class FinancialForecastReplayDataService : ServiceBase<FinancialForecastReplay>
    {
        public FinancialForecastReplayDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        /// <summary>Newest stored replay, or null when none has run yet.</summary>
        public async Task<FinancialForecastReplay?> GetLatestAsync() =>
            await Get(async set =>
                await Task.FromResult(set.OrderByDescending(r => r.CalculatedAt).FirstOrDefault()));
    }
}
