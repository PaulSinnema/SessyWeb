using SessyData.Model;

namespace SessyData.Services
{
    /// <summary>
    /// Provides access to stored daily gas prices.
    /// Abstracted as an interface to enable unit testing without a database.
    /// </summary>
    public interface IGasPricesDataService
    {
        Task UpsertAsync(GasPrice gasPrice);
        Task<double?> GetAverageMarketPriceAsync(DateTime? from = null);
        Task<GasPrice?> GetLatestAsync();
        Task<List<GasPrice>> GetAllAsync();
    }
}