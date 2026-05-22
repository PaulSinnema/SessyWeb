using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class GasPricesDataService : ServiceBase<GasPrice>, IGasPricesDataService
    {
        public GasPricesDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        /// <summary>
        /// Stores or updates the gas price for a given date.
        /// If a record already exists for that date, it is updated.
        /// </summary>
        public async Task UpsertAsync(GasPrice gasPrice)
        {
            await AddOrUpdate(new List<GasPrice> { gasPrice },
                (item, set) => set.FirstOrDefault(gp => gp.Date == item.Date));
        }

        /// <summary>
        /// Returns the average market price in EUR/m³ over all stored records,
        /// optionally filtered from a start date.
        /// Returns null when no records are available.
        /// </summary>
        public virtual async Task<double?> GetAverageMarketPriceAsync(DateTime? from = null)
        {
            var list = await GetList(async (set) =>
            {
                var query = from.HasValue
                    ? set.Where(gp => gp.Date >= from.Value)
                    : set;

                return await Task.FromResult(query.ToList());
            });

            if (list == null || !list.Any())
                return null;

            return list.Average(gp => gp.MarketPriceEurPerM3);
        }

        /// <summary>
        /// Returns the most recent stored gas price record, or null when none available.
        /// </summary>
        public virtual async Task<GasPrice?> GetLatestAsync()
        {
            return await Get(async (set) =>
            {
                return await Task.FromResult(
                    set.OrderByDescending(gp => gp.Date).FirstOrDefault());
            });
        }

        /// <summary>
        /// Returns all gas price records ordered by date ascending.
        /// </summary>
        public virtual async Task<List<GasPrice>> GetAllAsync()
        {
            return await GetList(async (set) =>
            {
                return await Task.FromResult(
                    set.OrderBy(gp => gp.Date).ToList());
            });
        }
    }
}