using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services.InverterServices;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Xml;

namespace SessyController.Services
{
    /// <summary>
    /// This background service fetches the prices from a Sessy battery..
    /// </summary>
    public class EPEXPricesService : BackgroundService, IDisposable, IEPEXPricesService
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

        // Enever.nl gas price feed (free, personal use, daily TTF price in EUR/m³)
        private const string EneverGasApiUrl = "https://enever.nl/apiv3/gasprijs_vandaag.php";
        private const string ConfigEneverTokenKey = "Enever:Token";

        private static string? _securityToken;
        private static string? _inDomain;
        private static string? _eneverToken;

        private ConcurrentDictionary<DateTime, DynamichSchedule>? _prices { get; set; }
        private static LoggingService<EPEXPricesService>? _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }
        private EPEXPricesDataService _epexPricesDataService { get; set; }
        private GasPricesDataService _gasPricesDataService { get; set; }

        private ExpectedPriceService _expectedPriceService;

        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }

        private IHttpClientFactory _httpClientFactory { get; set; }

        private CalculationService _calculationService { get; set; }

        private IServiceScope _scope { get; set; }

        private TaxesDataService _taxesService { get; set; }
        private SolarInverterManager _solarInverterManager { get; set; }

        private SettingsConfig _settingsConfig { get; set; }
        private IDisposable? _settingsConfigMonitorSubscription { get; set; }
        private Taxes? _taxes { get; set; }

        private bool PricesAvailable { get; set; } = false;
        private bool _initialized { get; set; } = false;

        /// <summary>
        /// The most recently fetched natural gas price in EUR per m³ (TTF day-ahead via Enever.nl).
        /// Null when not yet fetched or unavailable.
        /// </summary>
        public virtual double? CurrentGasPriceEurPerM3 { get; private set; }

        public EPEXPricesService(LoggingService<EPEXPricesService> logger,
                                    IConfiguration configuration,
                                    TimeZoneService timeZoneService,
                                    BatteryContainer batteryContainer,
                                    EPEXPricesDataService epexPricesDataService,
                                    GasPricesDataService gasPricesDataService,
                                    ExpectedPriceService expectedPriceService,
                                    IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                    TaxesDataService taxesService,
                                    SolarInverterManager solarInverterManager,
                                    CalculationService calculationService,
                                    IHttpClientFactory httpClientFactory,
                                    IServiceScopeFactory serviceScopeFactory)
        {
            _securityToken = configuration[ConfigSecurityTokenKey];
            _inDomain = configuration[ConfigInDomain];
            _eneverToken = configuration[ConfigEneverTokenKey];

            _timeZoneService = timeZoneService;
            _batteryContainer = batteryContainer;
            _serviceScopeFactory = serviceScopeFactory;
            _epexPricesDataService = epexPricesDataService;
            _gasPricesDataService = gasPricesDataService;
            _expectedPriceService = expectedPriceService;
            _solarInverterManager = solarInverterManager;
            _settingsConfigMonitor = settingsConfigMonitor;
            _httpClientFactory = httpClientFactory;
            _calculationService = calculationService;

            _scope = serviceScopeFactory.CreateScope();

            _taxesService = taxesService;

            _settingsConfig = _settingsConfigMonitor.CurrentValue;

            _settingsConfigMonitorSubscription = _settingsConfigMonitor.OnChange((settings) => _settingsConfig = settings);

            _logger = logger;
        }

        public bool IsInitialized()
        {
            return _initialized;
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

                    if (PricesAvailable)
                    {
                        // Wait until the next whole quarter or until cancellation
                        var localTime = _timeZoneService.Now;

                        delayTime = 15 - (localTime.Minute % 15) + 1; // 1 minute extra to be sure
                    }
                    else
                    {
                        _logger.LogWarning("No prices available from Sessy, checking again in 5 minutes.");
                    }

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
                await StorePrices();

                _initialized = true;
            }

            await FetchGasPriceAsync(cancellationToken);
        }

        /// <summary>
        /// Fetches today's TTF day-ahead gas price (EUR/m³) from Enever.nl.
        /// Updates <see cref="CurrentGasPriceEurPerM3"/> when successful.
        /// Logs a warning and leaves the previous value intact on failure.
        ///
        /// The Enever.nl feed is free for personal use; a token can be created at
        /// https://enever.nl/token-aanmaken/. Add it to appsettings.json as "Enever:Token".
        /// The price in field "prijsEGSI" is the TTF wholesale price (excl. taxes) in EUR/m³.
        /// </summary>
        private async Task FetchGasPriceAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_eneverToken))
            {
                _logger!.LogWarning("Enever token not configured — skipping gas price fetch. " +
                                    "Add 'Enever:Token' to appsettings.json.");
                return;
            }

            try
            {
                string url = $"{EneverGasApiUrl}?token={_eneverToken}";

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);

                var response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Expected JSON: {"status":"true","data":[{"datum":"...","prijsEGSI":"0.566598",...}]}
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.GetProperty("status").GetString() != "true")
                {
                    _logger!.LogWarning("Enever gas price feed returned status != true.");
                    return;
                }

                var data = root.GetProperty("data");

                if (data.GetArrayLength() == 0)
                {
                    _logger!.LogWarning("Enever gas price feed returned empty data array.");
                    return;
                }

                // Use "prijsEGSI" — the TTF wholesale (EGSI = End of Gas-Day Spot Index) price.
                string? rawPrice = data[0].GetProperty("prijsEGSI").GetString();

                if (double.TryParse(rawPrice, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out double marketPrice))
                {
                    // Store the daily market price in the database (upsert — one record per day).
                    var today = _timeZoneService.Now.Date;

                    await _gasPricesDataService.UpsertAsync(new SessyData.Model.GasPrice
                    {
                        Date = today,
                        MarketPriceEurPerM3 = marketPrice
                    });

                    // Apply gas energy tax (Energiebelasting) and VAT (BTW) from the Taxes table
                    // to convert the TTF market price to the all-in consumer price.
                    double? allInPrice = await _calculationService.CalculateGasPriceAsync(marketPrice);

                    CurrentGasPriceEurPerM3 = allInPrice ?? marketPrice;

                    _logger!.LogInformation(
                        $"Gas price fetched from Enever.nl: market={marketPrice:F4} EUR/m³, " +
                        $"all-in={CurrentGasPriceEurPerM3:F4} EUR/m³ (TTF EGSI + taxes)");
                }
                else
                {
                    _logger!.LogWarning($"Could not parse Enever gas price value: '{rawPrice}'");
                }
            }
            catch (Exception ex)
            {
                _logger!.LogWarning($"Could not fetch gas price from Enever.nl: {ex.Message}");
            }
        }

        private async Task FetchPricesFromSources(CancellationToken cancellationToken)
        {
            var now = _timeZoneService.Now.DateFloorQuarter();
            var lastDate = now;

            // Fetch day-ahead market prices from Sessy
            _prices = await FetchDayAheadPricesAsync();

            PricesAvailable = _prices != null && _prices.Count > 0;
        }

        private SemaphoreSlim GetPricesSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Get the fetched prices for yesterday, today and tomorrow (if present) as a sorted list.
        /// When tomorrow's prices are not yet available from the day-ahead market,
        /// fills them in with historical averages so the MILP planner can make
        /// informed decisions about today's battery management.
        /// </summary>
        public async Task<List<QuarterlyInfo>> GetPrices()
        {
            await GetPricesSemaphore.WaitAsync();

            try
            {
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

                if (data == null)
                    return new List<QuarterlyInfo>();

                // Check if tomorrow's prices are available.
                // Use explicit range comparison to avoid DateTimeKind mismatches.
                var tomorrow = _timeZoneService.Now.AddDays(1).Date;
                var tomorrowStart = tomorrow;
                var tomorrowEnd = tomorrow.AddDays(1);
                var tomorrowPrices = data
                    .Where(ep => ep.Time >= tomorrowStart && ep.Time < tomorrowEnd)
                    .ToList();

                if (tomorrowPrices.Count == 0)
                {
                    // Tomorrow's real prices are not yet available.
                    // Fill in with historical averages for planning purposes only.
                    var expectedPrices = await _expectedPriceService
                        .GetExpectedPricesForDateAsync(tomorrow)
                        .ConfigureAwait(false);

                    data.AddRange(expectedPrices);

                    // Pre-load into CalculationService cache so CalculateEnergyPrice()
                    // can find the expected prices without querying the database.
                    // Expected prices are never written to the database.
                    _calculationService.PreloadExpectedPrices(expectedPrices);

                    _logger.LogInformation(
                        $"Tomorrow's prices not yet available — using {expectedPrices.Count} " +
                        $"expected prices for {tomorrow:dd-MM-yyyy}.");
                }
                else
                {
                    // Real prices are available — clear any expected prices from cache
                    // so the real prices are used going forward.
                    _calculationService.InvalidateExpectedPrices();
                }

                var quarterlyInfos = await BuildQuarterliesAsync(data).ConfigureAwait(false);

                return quarterlyInfos;
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
                    _solarInverterManager,
                    _timeZoneService,
                    _calculationService));

            var hourlyInfos = (await Task.WhenAll(tasks)).ToList();

            // Mark quarters that use expected prices for visualization.
            // When HasExpectedPrices is true, all tomorrow's quarters are marked
            // so the UI can display them in a different color.
            if (_calculationService.HasExpectedPrices)
            {
                var tomorrow = _timeZoneService.Now.AddDays(1).Date;

                var tomorrowEnd = tomorrow.AddDays(1);

                foreach (var qi in hourlyInfos.Where(q => q.Time >= tomorrow && q.Time < tomorrowEnd))
                    qi.SetPriceExpected(true);

                _logger.LogInformation($"Marked as expected: {hourlyInfos.Count(q => q.IsPriceExpected)}");
            }

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
                client.Timeout = TimeSpan.FromSeconds(120);

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

        public class DynamichSchedule
        {
            public double Price { get; set; }
            public double Power { get; set; }
        }

        /// <summary>
        /// Get the day-ahead-prices from ENTSO-E.
        /// </summary>
        private async Task<ConcurrentDictionary<DateTime, DynamichSchedule>> FetchDayAheadPricesAsync()
        {
            var battery = _batteryContainer.Batteries!.First();

            var dynamicSchedule = await battery.GetDynamicScheduleAsync();

            var list = new ConcurrentDictionary<DateTime, DynamichSchedule>();

            foreach (var ep in dynamicSchedule.EnergyPrices)
            {
                var ds = new DynamichSchedule { Price = ep.Price / 100000.0 };

                ds.Power = dynamicSchedule!.DynamicSchedule!.SingleOrDefault(ds => ds.StartTime == ep.StartTime)?.Power ?? 0.0;

                var priceWattHour = ep.Price / 100000.0;

                if (!list.TryAdd(ep.StartTime, ds))
                {
                    list[ep.StartTime].Price = ds.Price;
                    list[ep.StartTime].Power = ds.Power;
                }
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
                    Price = keyValuePair.Value.Price
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