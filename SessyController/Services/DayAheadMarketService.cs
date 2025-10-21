using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.InverterServices;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
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
        private const string ConfigResolutionFormat = "ENTSO-E:ResolutionFormat"; // No longer in use
        private const string ConfigSecurityTokenKey = "ENTSO-E:SecurityToken";

        private static string? _securityToken;
        private static string? _inDomain;

        private ConcurrentDictionary<DateTime, double>? _prices { get; set; }
        private static LoggingService<DayAheadMarketService>? _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }
        private EPEXPricesDataService _epexPricesDataService { get; set; }
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }

        private IHttpClientFactory _httpClientFactory { get; set; }

        private CalculationService _calculationService { get; set; }

        private IServiceScope _scope { get; set; }

        private TaxesDataService _taxesService { get; set; }
        private SolarEdgeInverterService _solarEdgeService { get; set; }

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
                                    TaxesDataService taxesService,
                                    SolarEdgeInverterService solarEdgeService,
                                    CalculationService calculationService,
                                    IHttpClientFactory httpClientFactory,
                                    IServiceScopeFactory serviceScopeFactory)
        {
            _securityToken = configuration[ConfigSecurityTokenKey];
            _inDomain = configuration[ConfigInDomain];

            _timeZoneService = timeZoneService;
            _batteryContainer = batteryContainer;
            _serviceScopeFactory = serviceScopeFactory;
            _epexPricesDataService = epexPricesDataService;
            _solarEdgeService = solarEdgeService;
            _settingsConfigMonitor = settingsConfigMonitor;
            _httpClientFactory = httpClientFactory;
            _calculationService = calculationService;

            _scope = serviceScopeFactory.CreateScope();

            _taxesService = taxesService;

            _settingsConfig = _settingsConfigMonitor.CurrentValue;

            _settingsConfigMonitorSubscription = _settingsConfigMonitor.OnChange((settings) => _settingsConfig = settings);

            _logger = logger;
        }

        /// <summary>
        /// Executes the background service, fetching prices periodically.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _taxes = await _taxesService.GetTaxesForDate(_timeZoneService.Now.Date);

            _logger.LogWarning("EPEX Hourly Infos Service started ...");

            // await TemporaryRemoveAllNoneWholeHours();

            // Loop to fetch prices every day
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
                        // Wait until the next whole quarter or until cancellation
                        var localTime = _timeZoneService.Now;

                        delayTime = 15 - (localTime.Minute % 15) + 1; // 1 minute extra to be sure
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


        private async Task TemporaryRemoveAllNoneWholeHours()
        {
            var list = await _epexPricesDataService.GetList(async (set) =>
            {
                var october1 = new DateTime(2025, 10, 1);

                var result = set.Where(ep => ep.Time >= october1).ToList();

                return await Task.FromResult(result);
            });

            await _epexPricesDataService.Remove(list, (item, set) =>
            {
                return set.Where(pr => pr.Id == item.Id).FirstOrDefault();
            });
        }

        /// <summary>
        /// This routine is called periodically as a background task.
        /// </summary>
        public async Task Process(CancellationToken cancellationToken)
        {
            await FetchPricesFromSources(cancellationToken);

            if (PricesAvailable)
            {
                TransformPricesResolution();

                await StorePrices();
            }

            PricesInitialized = true;
        }

        /// <summary>
        /// Transform prices to 15 minutes resolution if needed.
        /// On 11 June 2025 the resolution for The Netherlands will change to 15 minutes. Until
        /// that time the 60 minutes resolution is transformed to 15 minutes.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        private void TransformPricesResolution()
        {
            if (PricesAvailable)
            {
                TransformPricesTo15MinuteResolutionIfNeeded();
            }
        }

        /// <summary>
        /// Add missing 15 minutes resolution entries.
        /// </summary>
        private void TransformPricesTo15MinuteResolutionIfNeeded()
        {
            var list = _prices!.ToList().OrderBy(ep => ep.Key);

            foreach (var price in list)
            {
                CheckQuarterPrices(price.Key.AddMinutes(15), price.Value);
                CheckQuarterPrices(price.Key.AddMinutes(30), price.Value);
                CheckQuarterPrices(price.Key.AddMinutes(45), price.Value);
            }
        }

        private void CheckQuarterPrices(DateTime time, double price)
        {
            if (!_prices.ContainsKey(time))
            {
                _prices.AddOrUpdate(time, price, Update);
            }
        }

        private double Update(DateTime time, double value)
        {
            throw new InvalidOperationException($"Value already exists time: {time}, value : {value}");
        }

        private async Task FetchPricesFromSources(CancellationToken cancellationToken)
        {
            var now = _timeZoneService.Now.DateFloorQuarter();
            var lastDate = now;

            // Fetch day-ahead market prices from Sessy
            _prices = await FetchDayAheadPricesAsync();

            if (_prices != null && _prices.Count > 0)
            {
                lastDate = _prices.Max(pr => pr.Key);
            }

            // It's 20:00 or later and still no prices on the Sessy.
            // if (_prices == null || (now.Hour >= 20 && (lastDate - now).Hours < 0))
            // {
            // _logger.LogWarning($"It's 20:00 or later, Sessy still has no prices. Falling back on ENTSO-E.");

            // Fallback on ENTSO-E for fetching the day-ahead market prices.
            var entsoePrices = await FetchDayAheadPricesAsync(now, 1, cancellationToken);

            var entsoeLastDate = lastDate;

            if (entsoePrices != null && entsoePrices.Count > 0)
            {
                entsoeLastDate = entsoePrices.Max(pr => pr.Key);
            }

            _logger.LogWarning($"Fallback on ENTSO-E Last date: {entsoeLastDate}, Sessy last date {lastDate}");

            if (entsoeLastDate > lastDate)
            {
                _logger.LogWarning($"ENTSO-E has more actual prices than Sessy, we take those prices.");
                _prices = entsoePrices;
            }
            else
            {
                _logger.LogWarning($"ENTSO-E prices are not more actual than Sessy, keep Sessy prices");
            }
            // }

            PricesAvailable = _prices != null && _prices.Count > 0;
        }

        private SemaphoreSlim GetPricesSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Get the fetched prices for yesterday, today and tomorrow (if present) as a sorted list.
        /// </summary>
        public async Task<List<QuarterlyInfo>> GetPrices()
        {
            await GetPricesSemaphore.WaitAsync();

            try
            {
                List<QuarterlyInfo> quarterlyInfos = new List<QuarterlyInfo>();

                var now = _timeZoneService.Now;
                var start = now.AddDays(-1).Date;
                var end = now.AddDays(1).Date.AddHours(23).AddMinutes(45);

                var data = await _epexPricesDataService.GetList(async (set) =>
                {
                    var result = set
                        .Where(ep => ep.Time >= start && ep.Time <= end)
                        .ToList();

                    return await Task.FromResult(result);
                });

                if (data != null)
                {
                    quarterlyInfos = await BuildQuarterliesAsync(data);

                    return quarterlyInfos;
                }

                return new List<QuarterlyInfo>();
            }
            finally
            {
                GetPricesSemaphore.Release();
            }
        }

        public async Task<List<QuarterlyInfo>> BuildQuarterliesAsync(List<EPEXPrices> list)
        {
            if (list == null) return new List<QuarterlyInfo>();

            var ordered = list.OrderBy(ep => ep.Time).ToList();

            var tasks = ordered.Select(ep =>
                QuarterlyInfo.CreateAsync(
                    ep.Time,
                    ep!.Price!.Value,
                    _settingsConfig,
                    _batteryContainer,
                    _solarEdgeService,
                    _timeZoneService,
                    _calculationService));

            var hourlyInfos = (await Task.WhenAll(tasks)).ToList();

            return hourlyInfos;
        }

        /// <summary>
        /// Get the prices and timestamps from the XML response.
        /// </summary>
        private ConcurrentDictionary<DateTime, double> GetPrices(string responseBody)
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
                            var startTimeNode = GetSingleNode(period, TagIntervalStart, nsmgr);
                            var startTime = DateTime.Parse(startTimeNode);
                            var resolution = GetSingleNode(period, TagResolution, nsmgr);

                            var startUtc = DateTimeOffset.Parse(startTimeNode, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

                            TimeSpan interval;

                            switch (resolution)
                            {
                                case "PT15M":
                                    interval = TimeSpan.FromMinutes(15);
                                    break;

                                case "PT60M":
                                    interval = TimeSpan.FromHours(1);
                                    break;

                                default:
                                    throw new InvalidOperationException($"Wrong resolution {resolution}");
                            }

                            var pointNodes = period.SelectNodes(TagPoint, nsmgr);

                            if (pointNodes != null)
                            {
                                foreach (XmlNode point in pointNodes)
                                {
                                    // Parse values culture-invariant
                                    int position = int.Parse(GetSingleNode(point, TagPosition, nsmgr), CultureInfo.InvariantCulture);
                                    var priceNode = GetSingleNode(point, TagPriceAmount, nsmgr);
                                    double price = double.Parse(priceNode, CultureInfo.InvariantCulture);

                                    // Calculate timestamp
                                    DateTimeOffset timestampUtc = startUtc.AddTicks(interval.Ticks * (position - 1));
                                    var timeZone = _timeZoneService.TimeZone;
                                    DateTime timestamp = TimeZoneInfo.ConvertTime(timestampUtc, timeZone).DateTime;

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
        private async Task<ConcurrentDictionary<DateTime, double>?> FetchDayAheadPricesAsync(DateTime date, int futureDays, CancellationToken cancellationToken)
        {
            // date = date.AddDays(-1);
            var pastDate = date.Date; // new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);
            string periodStartString = pastDate.ToString(FormatDate) + FormatTime;
            var futureDate = date.AddDays(futureDays);
            string periodEndString = futureDate.ToString(FormatDate) + FormatTime;
            string url = $"{ApiUrl}?documentType=A44&in_Domain={_inDomain}&out_Domain={_inDomain}&periodStart={periodStartString}&periodEnd={periodEndString}&securityToken={_securityToken}";

            var client = _httpClientFactory?.CreateClient();

            if (client != null)
            {
                HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                var prices = GetPrices(responseBody);

                if (prices.Count > 0)
                {
                    var endDate = prices.Keys.Max();

                    // Detect and fill gaps in the prices with average prices.
                    FillMissingPoints(prices, date.Date, endDate, TimeSpan.FromHours(1));

                    return prices;
                }

                return null;
            }

            _logger.LogError("Unable to create HttpClient");


            return new ConcurrentDictionary<DateTime, double>();
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        private async Task<ConcurrentDictionary<DateTime, double>> FetchDayAheadPricesAsync()
        {
            var battery = _batteryContainer.Batteries!.First();

            var dynamicSchedule = await battery.GetDynamicScheduleAsync();

            var list = new ConcurrentDictionary<DateTime, double>();

            foreach (var ep in dynamicSchedule.EnergyPrices)
            {
                var priceWattHour = ep.Price / 100000.0;

                list.AddOrUpdate(ep.StartTime, priceWattHour, (key, oldValue) => priceWattHour);
            }

            return list;
        }

        /// <summary>
        /// Store the prices in the database if not present.
        /// </summary>
        private async Task StorePrices()
        {
            var pricesList = new List<EPEXPrices>();

            foreach (var keyValuePair in _prices)
            {
                pricesList.Add(new EPEXPrices
                {
                    Time = keyValuePair.Key,
                    Price = keyValuePair.Value
                });
            }

            // Items already in the DB are updated!
            await _epexPricesDataService.AddOrUpdate(pricesList, (item, set) =>
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
                        _logger.LogInformation($"No price information available for {currentTime}");
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
