namespace SessyController.Services
{
    /// <summary>
    /// Provides energy price calculation methods.
    /// Abstracted as an interface to enable unit testing without a database.
    /// </summary>
    public interface ICalculationService
    {
        Task<double?> CalculateGasPriceAsync(double marketPriceEurPerM3);
    }
}