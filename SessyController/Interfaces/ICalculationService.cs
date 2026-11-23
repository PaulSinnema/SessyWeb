namespace SessyController.Services
{
    /// <summary>Buying and selling price for a quarter.</summary>
    public record EnergyPrice(double Buying, double Selling);

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
        Task<Dictionary<DateTime, EnergyPrice>> CalculateEnergyPricesBatchAsync(
            IEnumerable<DateTime> times);

        /// <summary>Buying prices only: skips the selling side and its daylight calculation.</summary>
        Task<Dictionary<DateTime, double>> CalculateBuyingPricesBatchAsync(IEnumerable<DateTime> times);
    }
}