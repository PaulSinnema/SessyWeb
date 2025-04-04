using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Migrations;
using SessyData.Model;
using SessyData.Services;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Xml;

namespace SessyController.Services
{
    /// <summary>
    /// This background service fetches the prices from a Sessy battery..
    /// </summary>
    public class DayAheadMarketService : BackgroundService, IDisposable
    {
        private const string ApiUrl = "https://web-api.tp.entsoe.eu/api";
        private const string FormatDate = "yyyyMMdd";
        private const string FormatTime = "0000";

        private const string TagNs = "urn:iec62325.351:tc57wg16:451-3:publicationdocument:7:3";
        private const string TagIntervalStart = "ns:timeInterval/ns:start";
        private const string TagPeriod = "ns:Period";
        private const string TagPoint = "ns:Point";
        private const string TagPosition = "ns:position";
        private const string TagPriceAmount = "ns:price.amount";
        private const string TagResolution = "ns:resolution";
        private const string TagTimeSeries = "//ns:TimeSeries";

        private const string ConfigInDomain = "ENTSO-E:InDomain"; // EIC-code
        private const string ConfigResolutionFormat = "ENTSO-E:ResolutionFormat";
        private const string ConfigSecurityTokenKey = "ENTSO-E:SecurityToken";

        private static string? _securityToken;
        private static string? _inDomain;
        private static string? _resolutionFormat;

        private ConcurrentDictionary<DateTime, double>? _prices { get; set; }
        private static LoggingService<DayAheadMarketService>? _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }
        private EPEXPricesDataService _epexPricesDataService { get; set; }
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }

        private IHttpClientFactory _httpClientFactory { get; set; }
        private IServiceScope _scope { get; set; }

        private TaxesService _taxesService { get; set; }
        private SolarEdgeService _solarEdgeService { get; set; }

        private SettingsConfig _settingsConfig { get; set; }
        private IDisposable? _settingsConfigMonitorSubscription { get; set; }
        private Taxes? _taxes { get; set; }

        public bool PricesAvailable { get; internal set; } = false;
        public bool PricesInitialized { get; internal set; } = false;

        public DayAheadMarketService(LoggingService<DayAheadMarketService> logger,
                                    IConfiguration configuration,
                                    TimeZoneService timeZoneService,
                                    BatteryContainer batteryContainer,
                                    EPEXPricesDataService epexPricesDataService,
                                    IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                    TaxesService taxesService,
                                    SolarEdgeService solarEdgeService,
                                    IHttpClientFactory httpClientFactory,
                                    IServiceScopeFactory serviceScopeFactory)
        {
            _securityToken = configuration[ConfigSecurityTokenKey];
            _inDomain = configuration[ConfigInDomain];
            _resolutionFormat = configuration[ConfigResolutionFormat];

            _timeZoneService = timeZoneService;
            _batteryContainer = batteryContainer;
            _serviceScopeFactory = serviceScopeFactory;
            _epexPricesDataService = epexPricesDataService;
            _solarEdgeService = solarEdgeService;
            _settingsConfigMonitor = settingsConfigMonitor;
            _httpClientFactory = httpClientFactory;

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
            var now = _timeZoneService.Now.DateHour();
            var lastDate = now;

            // Fetch day-ahead market prices
            _prices = await FetchDayAheadPricesAsync();

            if (_prices != null && _prices.Count > 0)
            {
                lastDate = _prices.Max(pr => pr.Key);
            }

            if (_prices == null || (now.Hour >= 20 && (lastDate - now).Hours < 4))
            {
                var entsoePrices = await FetchDayAheadPricesAsync(now, 2, cancellationToken);
                var entsoeLastDate = lastDate;

                if (entsoePrices != null && entsoePrices.Count >= 24)
                {
                    entsoeLastDate = entsoePrices.Max(pr => pr.Key);
                }

                if (entsoeLastDate > lastDate)
                    _prices = entsoePrices;
            }

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
                        .Select(ep => new HourlyInfo(ep.Time, GetBuyPrice(ep), GetBuyPrice(ep), _settingsConfig, _batteryContainer, _solarEdgeService, _timeZoneService))
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

        /// <summary>
        /// Get the prices and timestamps from the XML response.
        /// </summary>
        private static ConcurrentDictionary<DateTime, double> GetPrizes(string responseBody)
        {
            var prices = new ConcurrentDictionary<DateTime, double>();

            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(responseBody);

            XmlNamespaceManager nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
            nsmgr.AddNamespace("ns", TagNs);

            var timeSeriesNodes = xmlDoc.SelectNodes(TagTimeSeries, nsmgr);

            if (timeSeriesNodes != null)
            {
                foreach (XmlNode timeSeries in timeSeriesNodes)
                {
                    if (TagTimeSeries != null)
                    {
                        XmlNode? period = timeSeries.SelectSingleNode(TagPeriod, nsmgr);

                        if (period != null)
                        {

                            var startTime = DateTime.Parse(GetSingleNode(period, TagIntervalStart, nsmgr));
                            var resolution = GetSingleNode(period, TagResolution, nsmgr);
                            var interval = resolution == _resolutionFormat ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(15);
                            var pointNodes = period.SelectNodes(TagPoint, nsmgr);

                            if (pointNodes != null)
                            {
                                foreach (XmlNode point in pointNodes)
                                {
                                    int position = int.Parse(GetSingleNode(point, TagPosition, nsmgr));
                                    double price = double.Parse(GetSingleNode(point, TagPriceAmount, nsmgr));
                                    DateTime timestamp = startTime.Add(interval * (position));

                                    double priceWattHour = price / 1000;

                                    prices.AddOrUpdate(timestamp, priceWattHour, (key, oldValue) => priceWattHour);
                                }
                            }
                        }
                    }
                }
            }

            return prices;
        }

        /// <summary>
        /// Get a single node from a node. Returns an empty string if node was notfound.
        /// </summary>
        private static string GetSingleNode(XmlNode? node, string key, XmlNamespaceManager nsmgr)
        {
            if (node != null)
            {
                var singleNode = node.SelectSingleNode(key, nsmgr);

                if (singleNode != null)
                {
                    return singleNode.InnerText;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E Api.
        /// </summary>
        private async Task<ConcurrentDictionary<DateTime, double>> FetchDayAheadPricesAsync(DateTime date, int futureDays, CancellationToken cancellationToken)
        {
            date = date.AddDays(-1);
            var pastDate = date.Date; // new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);
            string periodStartString = pastDate.ToString(FormatDate) + FormatTime;
            var futureDate = date.AddDays(futureDays + 1);
            string periodEndString = futureDate.ToString(FormatDate) + FormatTime;
            string url = $"{ApiUrl}?documentType=A44&in_Domain={_inDomain}&out_Domain={_inDomain}&periodStart={periodStartString}&periodEnd={periodEndString}&securityToken={_securityToken}";

            var client = _httpClientFactory?.CreateClient();

            if (client != null)
            {
                HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                var prices = GetPrizes(responseBody);

                var endDate = prices.Keys.Max();

                // Detect and fill gaps in the prices with average prices.
                FillMissingPoints(prices, date.Date, endDate, TimeSpan.FromHours(1));

                StorePrices(prices);

                return prices;
            }

            _logger.LogError("Unable to create HttpClient");

            return new ConcurrentDictionary<DateTime, double>();
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

                // BUG: Sometime the Sessy returns 2 lists for the same date. The
                //      second list then contains all zero prices.
                if (!ep.Price!.All(pr => pr == 0))
                {
                    for (int hourIndex = 0; hourIndex < ep.Price.Count; hourIndex++)
                    {
                        var price = Convert.ToDouble(ep.Price[hourIndex]);
                        var priceWattHour = price / 100000.0;

                        list.AddOrUpdate(date.AddHours(hourIndex), priceWattHour, (key, oldValue) => priceWattHour);
                    }
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

            _epexPricesDataService.AddOrUpdate(statusList, (item, set) =>
            {
                return set.Where(sd => sd.Time == item.Time).SingleOrDefault(); // Contains
            });
        }

        /// <summary>
        /// Sometimes prices are missing. This routine fill the gaps with average prices.
        /// </summary>
        private static void FillMissingPoints(ConcurrentDictionary<DateTime, double> prices, DateTime periodStart, DateTime periodEnd, TimeSpan interval)
        {
            DateTime currentTime = periodStart;

            while (currentTime <= periodEnd)
            {
                if (!prices.ContainsKey(currentTime))
                {
                    // Search for the previous and future prices.
                    var previousPrice = GetPreviousPrice(prices, currentTime);

                    _logger.LogInformation($"Price missing for {currentTime}");

                    if (previousPrice.HasValue)
                    {
                        // Use previous prices in case the next price is missing
                        // Corresponded with ENTSO-E about the missing points.
                        // They are missing when it's the same price as the previous one.
                        prices[currentTime] = previousPrice.Value;
                    }
                    else
                    {
                        // Price information is missing. Write to log.
                        _logger.LogWarning($"No price information available for {currentTime}");
                    }
                }

                currentTime = currentTime.Add(interval);
            }
        }

        private static double? GetPreviousPrice(ConcurrentDictionary<DateTime, double> prices, DateTime timestamp)
        {
            var previousTimes = prices.Keys.Where(t => t < timestamp).OrderByDescending(t => t);
            return previousTimes.Any() ? prices[previousTimes.First()] : (double?)null;
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
