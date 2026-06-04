namespace SessyController.Services
{
    /// <summary>
    /// Provides energy price calculation methods.
    /// Abstracted as an interface to enable unit testing without a database.
    /// </summary>
    public interface ICalculationService
    {
        Task<double?> CalculateGasPriceAsync(double marketPriceEurPerM3);

        /// <summary>
        /// Batch-calculates buying and selling prices for a list of timestamps.
        /// More efficient than calling CalculateEnergyPrice per item.
        /// </summary>
        Task<Dictionary<DateTime, (double Buying, double Selling)>> CalculateEnergyPricesBatchAsync(
            IEnumerable<DateTime> times);
    }
}