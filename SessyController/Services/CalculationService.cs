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
        private EnergyHistoryService _energyHistoryService { get; set; }

        public CalculationService(EPEXPricesDataService epexPricesDataService,
                                  TimeZoneService timezoneService,
                                  TaxesDataService taxesDataService,
                                  EnergyHistoryService energyHistoryService)
        {
            _epexPricesDataService = epexPricesDataService;
            _timezoneService = timezoneService;
            _taxesDataService = taxesDataService;
            _energyHistoryService = energyHistoryService;
        }

        private async Task FillTaxesCache()
        {
            if (invalidateTaxesCacheDateTime < _timezoneService.Now)
            {
                taxesCache.Clear();

                var taxesList = await _taxesDataService.GetList(async (set) =>
                {
                    var result = set.ToList();

                    return await Task.FromResult(result);
                });

                foreach (var tax in taxesList)
                {
                    taxesCache.TryAdd(tax.Time.Value, tax);
                }

                invalidateTaxesCacheDateTime = _timezoneService.Now.AddSeconds(30);
            }
        }

        private ConcurrentDictionary<DateTime, EPEXPrices> epexPricesCache = new();
        private ConcurrentDictionary<DateTime, Taxes> taxesCache = new();
        private DateTime invalidateTaxesCacheDateTime { get; set; }
        private SemaphoreSlim calcuculateEnergyPriceSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Calculate the price including overhead cost if includeOverheadCosts = true.
        /// Returns null if prices, taxes or overhead cost are missing.
        /// </summary>
        public async Task<double?> CalculateEnergyPrice(DateTime time, bool buying, bool includeOverheadCosts = false)
        {
            await calcuculateEnergyPriceSemaphore.WaitAsync();

            try
            {
                await FillTaxesCache().ConfigureAwait(false);

                EPEXPrices? epexPrice;

                if (epexPricesCache.ContainsKey(time))
                {
                    epexPrice = epexPricesCache[time];
                }
                else
                {
                    epexPrice = await _epexPricesDataService.Get(async (set) =>
                        {
                            var result = set.FirstOrDefault(ep => ep.Time == time);

                            return await Task.FromResult(result);
                        });

                    epexPricesCache.TryAdd(time, epexPrice!);
                }

                // var taxes = await _taxesDataService.GetTaxesForDate(time);
                var cache = taxesCache.Where(tx => tx.Key <= time)
                        .OrderByDescending(tx => tx.Key)
                        .FirstOrDefault();

                var taxes = cache.Value;

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
                calcuculateEnergyPriceSemaphore.Release();
            }
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
