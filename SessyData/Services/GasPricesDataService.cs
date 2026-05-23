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
        /// Returns the heating-degree-day weighted average market gas price in EUR/m³.
        /// Quarters with lower temperatures (more heating demand) carry more weight,
        /// reflecting that the gas price matters most when consumption is highest.
        ///
        /// Formula: Σ(price × max(0, heatingThreshold - temp)) / Σ(max(0, heatingThreshold - temp))
        /// Falls back to a simple average when no temperature data is available.
        ///
        /// The heating threshold of 15.5°C is the standard Dutch stookgrens (heating threshold).
        /// </summary>
        public async Task<double?> GetHeatingWeightedAverageMarketPriceAsync(
            List<SessyData.Model.Consumption> consumptionData,
            double heatingThresholdCelsius = 15.5)
        {
            var gasPrices = await GetAllAsync();

            if (!gasPrices.Any())
                return null;

            // Build a lookup of daily average temperature from quarterly consumption data.
            var dailyTemps = consumptionData
                .GroupBy(c => c.Time.Date)
                .ToDictionary(
                    g => g.Key,
                    g => g.Average(c => c.Temperature));

            double weightedSum = 0.0;
            double totalWeight = 0.0;

            foreach (var gp in gasPrices)
            {
                double weight = 1.0; // Default: unweighted.

                if (dailyTemps.TryGetValue(gp.Date.Date, out var avgTemp))
                {
                    // Heating degree days: only count days colder than the threshold.
                    weight = Math.Max(0.0, heatingThresholdCelsius - avgTemp);
                }

                weightedSum += gp.MarketPriceEurPerM3 * weight;
                totalWeight += weight;
            }

            // Fall back to simple average when all weights are zero (e.g. summer-only data).
            if (totalWeight <= 0)
                return gasPrices.Average(gp => gp.MarketPriceEurPerM3);

            return weightedSum / totalWeight;
        }
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