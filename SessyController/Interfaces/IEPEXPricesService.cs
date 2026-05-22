namespace SessyController.Services
{
    /// <summary>
    /// Provides access to the current EPEX/Enever gas price.
    /// Abstracted as an interface to enable unit testing without a live service.
    /// </summary>
    public interface IEPEXPricesService
    {
        /// <summary>
        /// The most recently fetched natural gas price in EUR per m³ (TTF day-ahead via Enever.nl).
        /// Null when not yet fetched or unavailable.
        /// </summary>
        double? CurrentGasPriceEurPerM3 { get; }
    }
}