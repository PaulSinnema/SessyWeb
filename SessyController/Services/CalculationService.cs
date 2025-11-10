using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using System.Collections.Concurrent;

namespace SessyController.Services
{
    public class CalculationService
    {
        private EPEXPricesDataService _epexPricesDataService { get; set; }

        private TimeZoneService _timezoneService { get; set; }

        private TaxesDataService _taxesDataService { get; set; }
        private EnergyHistoryDataService _energyHistoryService { get; set; }

        public CalculationService(EPEXPricesDataService epexPricesDataService,
                                  TimeZoneService timezoneService,
                                  TaxesDataService taxesDataService,
                                  EnergyHistoryDataService energyHistoryService)
        {
            _epexPricesDataService = epexPricesDataService;
            _timezoneService = timezoneService;
            _taxesDataService = taxesDataService;
            _energyHistoryService = energyHistoryService;
        }

        /// <summary>
        /// Caches all tax records in the DB. The cache is invalidated every 30 seconds.
        /// </summary>
        private async Task FillTaxesCache()
        {
            if (_invalidateTaxesCacheDateTime < _timezoneService.Now)
            {
                _taxesCache.Clear();

                var taxesList = await _taxesDataService.GetList(async (set) =>
                {
                    var result = set.ToList();

                    return await Task.FromResult(result);
                });

                foreach (var tax in taxesList)
                {
                    _taxesCache.TryAdd(tax!.Time!.Value, tax);
                }

                _invalidateTaxesCacheDateTime = _timezoneService.Now.AddSeconds(30);
            }
        }

        private ConcurrentDictionary<DateTime, Taxes> _taxesCache = new();
        private DateTime _invalidateTaxesCacheDateTime { get; set; }

        private SemaphoreSlim _calcuculateEnergyPriceSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Calculate the energy price. Includes overhead cost if includeOverheadCosts = true.
        /// Returns null if prices, taxes or overhead cost are missing.
        /// </summary>
        public async Task<double?> CalculateEnergyPrice(DateTime time, bool buying, bool includeOverheadCosts = false)
        {
            await _calcuculateEnergyPriceSemaphore.WaitAsync();

            try
            {
                await FillTaxesCache().ConfigureAwait(false);

                (EPEXPrices? epexPrice, Taxes taxes) = await GetEpexPriceFromCache(time);

                if (epexPrice != null && taxes != null && epexPrice.Price.HasValue)
                {
                    var compensation = buying ? taxes.PurchaseCompensation : taxes.ReturnDeliveryCompensation;
                    var valueAddedTaxFactor = taxes.ValueAddedTax / 100 + 1;

                    var overheadCost = 0.0;

                    if (includeOverheadCosts)
                        overheadCost = GetOverheadCost(time, taxes) ?? 0.0;

                    var energyTax = 0.0;

                    if (taxes.Netting)
                    {
                        energyTax = taxes.EnergyTax;
                    }
                    else
                    {
                        if (buying)
                        {
                            energyTax = taxes.EnergyTax;
                        }
                    }

                    return (epexPrice.Price + overheadCost + energyTax + compensation) * valueAddedTaxFactor;
                }

                return null;
            }
            finally
            {
                _calcuculateEnergyPriceSemaphore.Release();
            }
        }

        private ConcurrentDictionary<DateTime, EPEXPrices> _epexPricesCache = new();
        private DateTime _invalidateEpexPricesCacheDateTime { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Retrieves the EPEXPrice from cache if present.
        /// If not present in the cache it is fetched from the table and added to the cache.
        /// The cache is invalidated every 24 hours.
        /// </summary>
        private async Task<(EPEXPrices? epexPrice, Taxes taxes)> GetEpexPriceFromCache(DateTime time)
        {
            EPEXPrices? epexPrice;

            if (_invalidateEpexPricesCacheDateTime < _timezoneService.Now)
            {
                _epexPricesCache.Clear();

                _invalidateEpexPricesCacheDateTime = _timezoneService.Now.AddHours(24);
            }

            if (_epexPricesCache.ContainsKey(time))
            {
                epexPrice = _epexPricesCache[time];
            }
            else
            {
                epexPrice = await _epexPricesDataService.Get(async (set) =>
                {
                    var result = set.FirstOrDefault(ep => ep.Time == time);

                    return await Task.FromResult(result);
                });

                _epexPricesCache.TryAdd(time, epexPrice!);
            }

            var cache = _taxesCache.Where(tx => tx.Key <= time)
                                   .OrderByDescending(tx => tx.Key)
                                   .FirstOrDefault();

            var taxes = cache.Value;
            return (epexPrice, taxes);
        }

        /// <summary>
        /// Calculate the overhead cost per quarter for the date.
        /// To determine if prices become negative these costs should NOT be included.
        /// </summary>
        private double? GetOverheadCost(DateTime date, Taxes taxRecord)
        {
            var quarters = (new DateTime(date.Year + 1, 1, 1) - new DateTime(date.Year, 1, 1)).Days * 24 * 4;

            var totalCost = taxRecord.NetManagementCost + taxRecord.FixedTransportFee + taxRecord.CapacityTransportFee;

            totalCost -= taxRecord.TaxReduction;

            return totalCost / quarters;
        }
    }
}
