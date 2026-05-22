using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Collections.Concurrent;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services
{
    public class CalculationService
    {
        private EPEXPricesDataService _epexPricesDataService { get; set; }

        private QuarterlyMeasurementDataService _measurementDataService { get; set; }

        private TimeZoneService _timezoneService { get; set; }

        private TaxesDataService _taxesDataService { get; set; }

        public CalculationService(EPEXPricesDataService epexPricesDataService,
                                  QuarterlyMeasurementDataService measurementDataService,
                                  TimeZoneService timezoneService,
                                  TaxesDataService taxesDataService)
        {
            _epexPricesDataService = epexPricesDataService;
            _measurementDataService = measurementDataService;
            _timezoneService = timezoneService;
            _taxesDataService = taxesDataService;
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

        /// <summary>
        /// Calculates the all-in consumer gas price in EUR/m³ from the TTF market price.
        /// Applies supplier markup, gas energy tax (Energiebelasting aardgas) and VAT (BTW)
        /// from the most recently applicable Taxes record.
        /// Formula: (marketPriceEurPerM3 + GasSupplierMarkupEurPerM3 + GasEnergyTaxEurPerM3)
        ///          × (1 + GasValueAddedTaxPct / 100)
        /// Returns null when no Taxes record is available.
        /// </summary>
        public async Task<double?> CalculateGasPriceAsync(double marketPriceEurPerM3)
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

            if (_invalidateEpexPricesCacheDateTime < _timezoneService.Now)
            {
                // Only clear if no expected prices are loaded — clearing the cache
                // while expected prices are active would lose them until the next
                // PreloadExpectedPrices() call.
                if (!_hasExpectedPrices)
                {
                    _epexPricesCache.Clear();
                }

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

                if (epexPrice != null)
                    _epexPricesCache.TryAdd(time, epexPrice);
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

        /// <summary>
        /// Calculates the average effective price of energy currently stored in the batteries.
        /// Uses QuarterlyMeasurement records to derive charge/discharge cost from actual prices
        /// and measured battery power.
        ///
        /// Positive result = average EUR/kWh paid for stored energy.
        /// Negative result = net revenue per kWh (discharged more than charged in period).
        /// </summary>
        public async Task<double> CalculateAveragePriceOfChargeInBatteries(
            double chargingCapacity, double dischargingCapacity, DateTime from, DateTime to)
        {
            var totalChargedKWh = 0.0;
            var totalCostEur = 0.0;

            var measurements = await _measurementDataService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= from && m.Time <= to)
                    .OrderBy(m => m.Time)
                    .ToList();

                return await Task.FromResult(result);
            });

            foreach (var m in measurements)
            {
                switch (m.BatteryMode)
                {
                    case SessyData.Model.BatteryMode.Charging:
                        // Energy charged = measured kWh capped at charging capacity.
                        var charged = Math.Min(m.BatteryChargedKWh, chargingCapacity / 1000.0 * 0.25);
                        totalChargedKWh += charged;
                        totalCostEur += charged * m.BuyingPriceEur;
                        break;

                    case SessyData.Model.BatteryMode.Discharging:
                        var discharged = Math.Min(m.BatteryDischargedKWh, dischargingCapacity / 1000.0 * 0.25);
                        totalChargedKWh -= discharged;
                        totalCostEur -= discharged * m.SellingPriceEur;
                        break;

                    case SessyData.Model.BatteryMode.ZeroNetHome:
                        // In ZeroNetHome the battery absorbs surplus solar or covers household load.
                        // Use net grid export as a proxy for energy delivered.
                        var netKWh = m.GridExportKWh - m.GridImportKWh;
                        totalChargedKWh -= netKWh;
                        totalCostEur -= netKWh * m.SellingPriceEur;
                        break;
                }
            }

            return totalChargedKWh > 0.0 ? totalCostEur / totalChargedKWh : 0.0;
        }

        /// <summary>
        /// Calculates available room in Watts for (dis)charging based on a QuarterlyMeasurement.
        /// Room = gap between current SOC and target SOC, converted to Watts.
        /// </summary>
        public static double CalculateRoomInWatt(
            QuarterlyMeasurement measurement,
            double chargingCapacityW,
            double dischargingCapacityW,
            double totalCapacityWh)
        {
            double roomInWatt = 0.0;

            switch (measurement.BatteryMode)
            {
                case SessyData.Model.BatteryMode.Charging:
                    // Room = capacity left to fill (Wh → W per quarter).
                    roomInWatt = (totalCapacityWh - measurement.BatteryStateOfChargeWh) * 4.0;
                    roomInWatt = Math.Min(roomInWatt, chargingCapacityW);
                    break;

                case SessyData.Model.BatteryMode.Discharging:
                    // Room = energy available to discharge.
                    roomInWatt = measurement.BatteryStateOfChargeWh * 4.0;
                    roomInWatt = Math.Min(roomInWatt, dischargingCapacityW);
                    break;

                case SessyData.Model.BatteryMode.ZeroNetHome:
                    // Room = SOC available for self-consumption balancing.
                    roomInWatt = (measurement.BatteryStateOfChargeWh + measurement.SolarProductionKWh * 1000.0) * 4.0;
                    roomInWatt = Math.Min(roomInWatt, dischargingCapacityW);
                    break;

                case SessyData.Model.BatteryMode.Disabled:
                default:
                    roomInWatt = 0.0;
                    break;
            }

            return Math.Max(roomInWatt, 0.0);
        }
    }
}