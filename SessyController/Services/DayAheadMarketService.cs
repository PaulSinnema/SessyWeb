using System;
using System.Collections.Concurrent;
using System.Xml;
using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;

namespace SessyController.Services
{
    /// <summary>
    /// This background service fetches the prices from ENTSO-E.
    /// </summary>
    public class DayAheadMarketService : BackgroundService
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
        private volatile ConcurrentDictionary<DateTime, double>? _prices;
        private static IHttpClientFactory? _httpClientFactory;
        private SettingsConfig _settingsConfig;
        private static LoggingService<DayAheadMarketService>? _logger;
        private readonly TimeZoneService _timeZoneService;

        public bool PricesAvailable { get; internal set; } = false;
        public bool PricesInitialized { get; internal set; } = false;

        public DayAheadMarketService(LoggingService<DayAheadMarketService> logger,
                                    IConfiguration configuration,
                                    IWebHostEnvironment environment,
                                    IHttpClientFactory httpClientFactory,
                                    IOptions<SettingsConfig> settingsConfig,
                                    TimeZoneService timeZoneService)
        {
            _securityToken = configuration[ConfigSecurityTokenKey];
            _inDomain = configuration[ConfigInDomain];
            _resolutionFormat = configuration[ConfigResolutionFormat];
            _httpClientFactory = httpClientFactory;
            _settingsConfig = settingsConfig.Value;
            _timeZoneService = timeZoneService;
            _logger = logger;
        }

        /// <summary>
        /// Executes the background service, fetching prices periodically.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogInformation("EPEXHourlyInfosService started.");

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
            }

            _logger.LogInformation("EPEXHourlyInfosService stopped.");

        }

        /// <summary>
        /// This routine is called periodicly as a background task.
        /// </summary>
        public async Task Process(CancellationToken cancellationToken)
        {
            // Fetch day-ahead market prices
            _prices = await FetchDayAheadPricesAsync(DateTime.UtcNow.AddDays(-1), 2, cancellationToken);

            PricesAvailable = _prices != null && _prices.Count > 0;

            PricesInitialized = true;
        }

        /// <summary>
        /// Get the fetched prices for today and tomorrow (if present) as a sorted dictionary.
        /// </summary>
        public List<HourlyInfo> GetPrices()
        {
            List<HourlyInfo> hourlyInfos = new List<HourlyInfo>();

            if (_prices != null)
            {
                hourlyInfos = _prices.OrderBy(vk => vk.Key)
                    .Select(vk => new HourlyInfo { Time = vk.Key, Price = vk.Value })
                    .ToList();

                return hourlyInfos;
            }

            return new List<HourlyInfo>();
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E Api.
        /// </summary>
        private static async Task<ConcurrentDictionary<DateTime, double>> FetchDayAheadPricesAsync(DateTime date, int futureDays, CancellationToken cancellationToken)
        {
            date = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0);
            string periodStartString = date.ToString(FormatDate) + FormatTime;
            var futureDate = date.AddDays(futureDays);
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
                FillMissingPoints(prices, date, endDate, TimeSpan.FromHours(1));

                return prices;
            }

            _logger.LogError("Unable to create HttpClient");

            return new ConcurrentDictionary<DateTime, double>();
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
                    var nextPrice = GetNextPrice(prices, currentTime);

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

        private static double? GetNextPrice(ConcurrentDictionary<DateTime, double> prices, DateTime timestamp)
        {
            var nextTimes = prices.Keys.Where(t => t > timestamp).OrderBy(t => t);
            return nextTimes.Any() ? prices[nextTimes.First()] : (double?)null;
        }
    }
}
