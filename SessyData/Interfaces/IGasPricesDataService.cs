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
        Task<double?> GetHeatingWeightedAverageMarketPriceAsync(
            List<SessyData.Model.Consumption> consumptionData,
            double heatingThresholdCelsius = 15.5);
        Task<GasPrice?> GetLatestAsync();
        Task<List<GasPrice>> GetAllAsync();
    }
}