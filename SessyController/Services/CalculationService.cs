using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Collections.Concurrent;
using SessyCommon.Enums;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services
{
    public class CalculationService : ICalculationService
    {
        private EPEXPricesDataService _epexPricesDataService { get; set; }

        private QuarterlyMeasurementDataService _measurementDataService { get; set; }

        private TimeZoneService _timezoneService { get; set; }

        private TaxesDataService _taxesDataService { get; set; }

        private SettingsService _settingsService { get; set; }

        public CalculationService(EPEXPricesDataService epexPricesDataService,
                                  QuarterlyMeasurementDataService measurementDataService,
                                  TimeZoneService timezoneService,
                                  TaxesDataService taxesDataService,
                                  SettingsService settingsService)
        {
            _epexPricesDataService = epexPricesDataService;
            _measurementDataService = measurementDataService;
            _timezoneService = timezoneService;
            _taxesDataService = taxesDataService;
            _settingsService = settingsService;
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
        /// Pre-populates the EPEX prices cache with expected prices for dates
        /// not yet available in the database. Must be called before
        /// BuildQuarterliesAsync when using expected prices.
        /// Expected prices are never stored in the database — they are only
        /// used for planning purposes until real prices become available.
        /// </summary>
        public void PreloadExpectedPrices(IEnumerable<EPEXPrices> expectedPrices)
        {
            foreach (var price in expectedPrices)
            {
                // TryAdd ensures existing real prices are never overwritten.
                _epexPricesCache.TryAdd(price.Time, price);
            }

            _hasExpectedPrices = true;
        }

        /// <summary>
        /// Clears expected prices from the cache when real prices become available.
        /// The cache will be repopulated from the database on the next request.
        /// </summary>
        public void InvalidateExpectedPrices()
        {
            if (_hasExpectedPrices)
            {
                _epexPricesCache.Clear();
                _invalidateEpexPricesCacheDateTime = DateTime.MinValue;
                _hasExpectedPrices = false;
            }
        }

        // Flag to track whether expected prices have been preloaded.
        private bool _hasExpectedPrices = false;

        /// <summary>
        /// True when expected prices have been preloaded into the cache.
        /// Used by BuildQuarterliesAsync to mark quarters for visualization.
        /// </summary>
        public bool HasExpectedPrices => _hasExpectedPrices;

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
                    var overheadCost = includeOverheadCosts ? GetOverheadCost(time, taxes) ?? 0.0 : 0.0;

                    return buying
                        ? EnergyPriceCalculator.Buy(taxes, epexPrice.Price.Value, overheadCost)
                        : EnergyPriceCalculator.Sell(taxes, epexPrice.Price.Value, IsDaylight(time), overheadCost);
                }

                return null;
            }
            finally
            {
                _calcuculateEnergyPriceSemaphore.Release();
            }
        }

        /// <summary>Buying and selling prices for a batch of quarters, see PriceBatchAsync.</summary>
        public async Task<Dictionary<DateTime, EnergyPrice>> CalculateEnergyPricesBatchAsync(
            IEnumerable<DateTime> times)
        {
            var priced = await PriceBatchAsync(times, includeSelling: true).ConfigureAwait(false);

            return priced.ToDictionary(p => p.Time, p => new EnergyPrice(p.Buying, p.Selling));
        }

        public async Task<Dictionary<DateTime, double>> CalculateBuyingPricesBatchAsync(IEnumerable<DateTime> times)
        {
            var priced = await PriceBatchAsync(times, includeSelling: false).ConfigureAwait(false);

            return priced.ToDictionary(p => p.Time, p => p.Buying);
        }

        /// <summary>
        /// Prices a batch of quarters. The shared lock is held only to refresh the caches: the prices
        /// of the range come in one query into a local table, and the quarters are priced in parallel
        /// without the lock. A year of quarters used to keep the lock for seconds, and every other price
        /// calculation in the application, the planner's included, waited behind it.
        /// </summary>
        private async Task<List<(DateTime Time, double Buying, double Selling)>> PriceBatchAsync(IEnumerable<DateTime> times, bool includeSelling)
        {
            var timeList = times.Distinct().ToList();

            if (timeList.Count == 0)
                return new();

            KeyValuePair<DateTime, Taxes>[] taxes;

            await _calcuculateEnergyPriceSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                await FillTaxesCache().ConfigureAwait(false);
                ExpireEpexPricesCache();

                // Snapshot under the lock: FillTaxesCache clears and refills the shared cache.
                taxes = _taxesCache.OrderBy(tx => tx.Key).ToArray();
            }
            finally
            {
                _calcuculateEnergyPriceSemaphore.Release();
            }

            var from = timeList.Min();
            var to = timeList.Max();

            var rows = await _epexPricesDataService.GetList(set =>
                Task.FromResult(set.Where(ep => ep.Time >= from && ep.Time <= to && ep.Price != null).ToList())).ConfigureAwait(false);

            // First row per quarter, as the single-quarter lookup does.
            var epex = new Dictionary<DateTime, double>();
            foreach (var row in rows)
                epex.TryAdd(row.Time, row.Price!.Value);

            // Tax records ordered by start, for a binary search per quarter.
            var taxStarts = taxes.Select(tx => tx.Key).ToArray();

            var result = new (DateTime Time, double Buying, double Selling)?[timeList.Count];

            Parallel.For(0, timeList.Count, i =>
            {
                var time = timeList[i];

                // Database price first; a future quarter can still carry an expected price in the cache.
                double market;
                if (epex.TryGetValue(time, out var price))
                    market = price;
                else if (_epexPricesCache.TryGetValue(time, out var expected) && expected.Price.HasValue)
                    market = expected.Price.Value;
                else
                    return;

                var index = Array.BinarySearch(taxStarts, time);
                if (index < 0) index = ~index - 1;
                if (index < 0) return;

                var tax = taxes[index].Value;

                result[i] = (time,
                    EnergyPriceCalculator.Buy(tax, market),
                    includeSelling ? EnergyPriceCalculator.Sell(tax, market, IsDaylight(time)) : 0.0);
            });

            return result.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        }

        /// <summary>
        /// The all-in consumer gas price in EUR/m³ from the TTF market price: supplier markup, gas energy
        /// tax and VAT from the most recently applicable Taxes record.
        /// Formula: (marketPriceEurPerM3 + GasSupplierMarkupEurPerM3 + GasEnergyTaxEurPerM3) × (1 + GasValueAddedTaxPct / 100)
        /// </summary>
        public virtual async Task<double?> CalculateGasPriceAsync(double marketPriceEurPerM3)
        {
            await FillTaxesCache().ConfigureAwait(false);

            var now = _timezoneService.Now;

            var cache = _taxesCache.Where(tx => tx.Key <= now)
                                   .OrderByDescending(tx => tx.Key)
                                   .FirstOrDefault();

            var taxes = cache.Value;

            if (taxes == null)
                return null;

            double vatFactor = taxes.GasValueAddedTaxPct / 100.0 + 1.0;

            return (marketPriceEurPerM3 + taxes.GasSupplierMarkupEurPerM3 + taxes.GasEnergyTaxEurPerM3) * vatFactor;
        }

        private ConcurrentDictionary<DateTime, EPEXPrices> _epexPricesCache = new();
        private DateTime _invalidateEpexPricesCacheDateTime { get; set; } = DateTime.MinValue;

        /// <summary>The tax record in force at the given time.</summary>
        private Taxes? TaxesAt(DateTime time) =>
            _taxesCache.Where(tx => tx.Key <= time).OrderByDescending(tx => tx.Key).FirstOrDefault().Value;

        /// <summary>Clears the EPEX cache once a day, unless expected prices are loaded into it.</summary>
        private void ExpireEpexPricesCache()
        {
            if (_invalidateEpexPricesCacheDateTime >= _timezoneService.Now)
                return;

            // Clearing while expected prices are active would lose them until the next PreloadExpectedPrices().
            if (!_hasExpectedPrices)
                _epexPricesCache.Clear();

            _invalidateEpexPricesCacheDateTime = _timezoneService.Now.AddHours(24);
        }

        /// <summary>
        /// Retrieves the EPEXPrice from cache if present.
        /// If not present in the cache it is fetched from the table and added to the cache.
        /// The cache is invalidated every 24 hours.
        /// Note: expected prices preloaded via PreloadExpectedPrices() are never
        /// overwritten by database values — they are cleared via InvalidateExpectedPrices()
        /// when real prices become available.
        /// </summary>
        private async Task<(EPEXPrices? epexPrice, Taxes taxes)> GetEpexPriceFromCache(DateTime time)
        {
            EPEXPrices? epexPrice;

            ExpireEpexPricesCache();

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

                if (epexPrice != null)
                    _epexPricesCache.TryAdd(time, epexPrice);
            }

            return (epexPrice, TaxesAt(time)!);
        }

        /// <summary>
        /// True when the given local time falls between sunrise and sunset at the
        /// configured location. Used for the Zonnebonus (e.g. Zonneplan): works for
        /// past, present and future quarters, unlike TimeZoneService's "now"-only overload.
        /// </summary>
        private bool IsDaylight(DateTime time)
        {
            var settings = _settingsService.Current;

            // The sun position is costly (tens of µs) and a quarter's answer never changes for a location.
            return _daylightCache.GetOrAdd((time, settings.Latitude, settings.Longitude), key =>
                _timezoneService.GetSunlightLevel(key.Time, key.Latitude, key.Longitude) == SolCalc.Data.SunlightLevel.Daylight);
        }

        private readonly ConcurrentDictionary<(DateTime Time, double Latitude, double Longitude), bool> _daylightCache = new();

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
