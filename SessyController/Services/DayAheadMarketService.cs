using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Collections.Concurrent;

namespace SessyController.Services
{
    /// <summary>
    /// This background service fetches the prices from a Sessy battery..
    /// </summary>
    public class DayAheadMarketService : BackgroundService, IDisposable
    {
        private ConcurrentDictionary<DateTime, double>? _prices { get; set; }
        private static LoggingService<DayAheadMarketService>? _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }
        private EPEXPricesDataService _epexPricesDataService { get; set; }
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }

        private IServiceScope _scope;

        private TaxesService _taxesService { get; set; }

        private SettingsConfig _settingsConfig { get; set; }
        private IDisposable? _settingsConfigMonitorSubscription { get; set; }
        private Taxes? _taxes { get; set; }

        public bool PricesAvailable { get; internal set; } = false;
        public bool PricesInitialized { get; internal set; } = false;

        public DayAheadMarketService(LoggingService<DayAheadMarketService> logger,
                                    TimeZoneService timeZoneService,
                                    BatteryContainer batteryContainer,
                                    EPEXPricesDataService epexPricesDataService,
                                    IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                    TaxesService taxesService,
                                    IServiceScopeFactory serviceScopeFactory)
        {
            _timeZoneService = timeZoneService;
            _batteryContainer = batteryContainer;
            _serviceScopeFactory = serviceScopeFactory;
            _epexPricesDataService = epexPricesDataService;
            _settingsConfigMonitor = settingsConfigMonitor;

            _scope = serviceScopeFactory.CreateScope();

            _taxesService = taxesService;

            _taxes = _taxesService.GetTaxesForDate(_timeZoneService.Now.Date);

            _settingsConfig = _settingsConfigMonitor.CurrentValue;

            _settingsConfigMonitorSubscription = _settingsConfigMonitor.OnChange((settings) => _settingsConfig = settings);

            _logger = logger;
        }

        /// <summary>
        /// Executes the background service, fetching prices periodically.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("EPEX Hourly Infos Service started ...");

            // Loop to fetch prices every 24 hours
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while processing EPEX prices.");
                }

                try
                {
                    int delayTime = 5; // Check again in 5 minutes if no prices available

                    if (_prices != null && _prices.Count > 0)
                    {
                        // Wait until the next whole hour or until cancellation
                        var localTime = _timeZoneService.Now;

                        delayTime = 60 - localTime.Minute + 1; // 1 minute extra to be sure
                    }
                    else
                        _logger.LogWarning("No prices available from ENTSO-E, checking again in 5 minutes.");

                    await Task.Delay(TimeSpan.FromMinutes(delayTime), cancelationToken);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay, keep processing {ex.ToDetailedString()}");
                }
            }

            _logger.LogWarning("EPEX HourlyInfos Service stopped.");
        }

        /// <summary>
        /// This routine is called periodicly as a background task.
        /// </summary>
        public async Task Process(CancellationToken cancellationToken)
        {
            var now = _timeZoneService.Now;

            // Fetch day-ahead market prices
            _prices = await FetchDayAheadPricesAsync();

            PricesAvailable = _prices != null && _prices.Count > 0;

            PricesInitialized = true;
        }

        private SemaphoreSlim GetPricesSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Get the fetched prices for yesterday, today and tomorrow (if present) as a sorted list.
        /// </summary>
        public List<HourlyInfo> GetPrices()
        {
            GetPricesSemaphore.Wait();

            try
            {
                List<HourlyInfo> hourlyInfos = new List<HourlyInfo>();

                var now = _timeZoneService.Now;
                var start = now.AddDays(-1).Date;
                var end = now.AddDays(1).Date.AddHours(23);

                var data = _epexPricesDataService.GetList((set) =>
                {
                    return set
                        .Where(ep => ep.Time >= start && ep.Time <= end)
                        .ToList();
                });

                if (data != null)
                {
                    hourlyInfos = data.OrderBy(ep => ep.Time)
                        .Select(ep => new HourlyInfo(ep.Time, GetBuyPrice(ep), GetBuyPrice(ep), _settingsConfig))
                        .ToList();

                    return hourlyInfos;
                }

                return new List<HourlyInfo>();
            }
            finally
            {
                GetPricesSemaphore.Release();
            }
        }

        public double GetBuyPrice(EPEXPrices epexPrices)
        {
            return GetPrice(epexPrices, _taxes.PurchaseCompensation);
        }

        public double GetSellPrice(EPEXPrices epexPrices)
        {
            return GetPrice(epexPrices, _taxes.ReturnDeliveryCompensation);
        }

        private double GetPrice(EPEXPrices epexPrices, double compensation)
        {
            return ((epexPrices.Price!.Value + _taxes.EnergyTax + compensation) * (100 + _taxes.ValueAddedTax)) / 100;
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        private async Task<ConcurrentDictionary<DateTime, double>> FetchDayAheadPricesAsync()
        {
            var battery = _batteryContainer.Batteries!.First();

            var dynamicSchedule = await battery.GetDynamicScheduleAsync();

            var list = new ConcurrentDictionary<DateTime, double>();

            for (int dateIndex = 0; dateIndex < dynamicSchedule.EnergyPrices.Count; dateIndex++)
            {
                var ep = dynamicSchedule.EnergyPrices[dateIndex];
                var date = Convert.ToDateTime(ep.Date);

                for (int hourIndex = 0; hourIndex < ep.Price.Count; hourIndex++)
                {
                    var price = Convert.ToDouble(ep.Price[hourIndex]);
                    var priceWattHour = price / 100000.0;

                    list.AddOrUpdate(date.AddHours(hourIndex), priceWattHour, (key, oldValue) => priceWattHour);
                }
            }

            StorePrices(list);

            return list;
        }

        /// <summary>
        /// Store the prices in the database if not present.
        /// </summary>
        private void StorePrices(ConcurrentDictionary<DateTime, double> prices)
        {
            var statusList = new List<EPEXPrices>();

            foreach (var keyValuePair in prices)
            {
                statusList.Add(new EPEXPrices
                {
                    Time = keyValuePair.Key,
                    Price = keyValuePair.Value
                });
            }

            _epexPricesDataService.Add(statusList, (item, set) =>
            {
                return set.Any(sd => sd.Time == item.Time); // Contains
            });
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _settingsConfigMonitorSubscription.Dispose();

                _isDisposed = true;
            }

            base.Dispose();
        }
    }
}
