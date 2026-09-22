using Microsoft.Extensions.Configuration;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using System.Net.Http;

namespace SessyController.Services
{
    /// <summary>
    /// Fetches today's TTF day-ahead gas price (EUR/m³) from Enever.nl once a day, with backoff on
    /// failure. Isolated from EPEXPricesService so the gas path stands on its own.
    ///
    /// The Enever.nl feed is free for personal use; a token can be created at
    /// https://enever.nl/token-aanmaken/. Add it to appsettings.json as "Enever:Token".
    /// The price in field "prijsEGSI" is the TTF wholesale price (excl. taxes) in EUR/m³.
    /// </summary>
    public class GasPriceService : BackgroundService, IGasPriceService
    {
        // Enever.nl gas price feed (free, personal use, daily TTF price in EUR/m³)
        private const string EneverGasApiUrl = "https://enever.nl/apiv3/gasprijs_vandaag.php";
        private const string ConfigEneverTokenKey = "Enever:Token";

        private readonly LoggingService<GasPriceService> _logger;
        private readonly TimeZoneService _timeZoneService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly GasPricesDataService _gasPricesDataService;
        private readonly CalculationService _calculationService;
        private readonly NotificationService _notificationService;

        private readonly string? _eneverToken;

        // Gas fetch backoff: the free Enever feed is daily and rate-limited, so a failing fetch
        // must not keep hammering.
        private int _gasFailureCount = 0;
        private DateTime? _nextGasAttempt = null;

        public GasPriceService(LoggingService<GasPriceService> logger,
                               IConfiguration configuration,
                               TimeZoneService timeZoneService,
                               IHttpClientFactory httpClientFactory,
                               GasPricesDataService gasPricesDataService,
                               CalculationService calculationService,
                               NotificationService notificationService)
        {
            _logger = logger;
            _timeZoneService = timeZoneService;
            _httpClientFactory = httpClientFactory;
            _gasPricesDataService = gasPricesDataService;
            _calculationService = calculationService;
            _notificationService = notificationService;

            _eneverToken = configuration[ConfigEneverTokenKey];
        }

        /// <summary>
        /// The most recently fetched natural gas price in EUR per m³ (TTF day-ahead via Enever.nl).
        /// Null when not yet fetched or unavailable.
        /// </summary>
        public virtual double? CurrentGasPriceEurPerM3 { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("Gas price service started ...");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await FetchGasPriceAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while fetching the gas price.");
                }

                try
                {
                    // The price is a daily value (cached per day in the DB) and the feed is
                    // rate-limited, so a modest poll plus the backoff below is plenty.
                    await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
                }
                catch (TaskCanceledException)
                {
                }
            }

            _logger.LogWarning("Gas price service stopped.");
        }

        private async Task FetchGasPriceAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_eneverToken))
            {
                _logger!.LogWarning("Enever token not configured — skipping gas price fetch. " +
                                    "Add 'Enever:Token' to appsettings.json.");
                return;
            }

            var gasPrice = await _gasPricesDataService.Get(async set =>
            {
                var result = set.Where(gp => gp.Date.Date == _timeZoneService.Now.Date).FirstOrDefault();

                return await Task.FromResult(result);
            });

            double marketPrice = 0.00;

            // Whether we ended up with a real price. Without this the failure paths below fall
            // through to the tax calculation with marketPrice still 0.00 and publish taxes-only
            // as the gas price — the opposite of what this method documents, and silent.
            bool havePrice = false;

            var today = _timeZoneService.Now.Date;

            // Respect the backoff: skip the fetch attempt until the scheduled retry time.
            if (gasPrice == null && _nextGasAttempt != null && _timeZoneService.Now < _nextGasAttempt.Value)
                return;

            if (gasPrice == null)
            {
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
                        await _notificationService.AddAsync(
                            NotificationSeverity.Error, "Gas", "Gas price fetch failed",
                            "Enever.nl returned status != true.", dedupKey: "gas-fetch-failed");
                        ScheduleGasBackoff(false);
                        return;
                    }

                    var data = root.GetProperty("data");

                    // On a rate-limit the free feed answers 200 with "data" as a STRING error
                    // message (code 6) instead of the usual array. Handle it explicitly, otherwise
                    // GetArrayLength() throws a confusing "Array vs String" exception.
                    if (data.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        string apiMessage = data.ValueKind == System.Text.Json.JsonValueKind.String
                            ? data.GetString() ?? "unknown error"
                            : $"unexpected data type {data.ValueKind}";
                        string code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() ?? "" : "";
                        bool tokenLimit = code == "6" ||
                            apiMessage.Contains("token limit", StringComparison.OrdinalIgnoreCase);

                        string reason = tokenLimit
                            ? "Enever.nl API token limit exceeded — resets on the 1st of the month."
                            : $"Enever.nl: {apiMessage}";

                        _logger!.LogWarning($"Enever gas price feed error (code {code}): {apiMessage}");
                        await _notificationService.AddAsync(
                            NotificationSeverity.Error, "Gas", "Gas price fetch failed",
                            reason, dedupKey: "gas-fetch-failed");
                        ScheduleGasBackoff(tokenLimit);
                        return;
                    }

                    if (data.GetArrayLength() == 0)
                    {
                        _logger!.LogWarning("Enever gas price feed returned empty data array.");
                        await _notificationService.AddAsync(
                            NotificationSeverity.Error, "Gas", "Gas price fetch failed",
                            "Enever.nl returned an empty data array.", dedupKey: "gas-fetch-failed");
                        ScheduleGasBackoff(false);
                        return;
                    }

                    // Use "prijsEGSI" — the TTF wholesale (EGSI = End of Gas-Day Spot Index) price.
                    string? rawPrice = data[0].GetProperty("prijsEGSI").GetString();

                    if (double.TryParse(rawPrice, System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out marketPrice))
                    {
                        // Store the daily market price in the database (upsert — one record per day).
                        await _gasPricesDataService.UpsertAsync(new SessyData.Model.GasPrice
                        {
                            Date = today,
                            MarketPriceEurPerM3 = marketPrice
                        });

                        havePrice = true;
                    }
                    else
                    {
                        _logger!.LogWarning($"Could not parse Enever gas price value: '{rawPrice}'");
                    }
                }
                catch (Exception ex)
                {
                    // Also catches a missing "prijsEGSI" field: GetProperty throws rather than
                    // returning null, so a feed that answers 200 with a different shape lands here.
                    _logger!.LogWarning($"Could not fetch gas price from Enever.nl: {ex.Message}");
                }
            }
            else
            {
                marketPrice = gasPrice.MarketPriceEurPerM3;
                havePrice = true;
            }

            // Keep the last known price when the feed gave nothing usable. Publishing a price
            // built on a market price of zero would show the heat-pump comparison a gas price of
            // taxes alone, which is worse than showing yesterday's.
            if (!havePrice)
            {
                await _notificationService.AddAsync(
                    NotificationSeverity.Error, "Gas", "Gas price fetch failed",
                    "Could not fetch a usable gas price from Enever.nl.", dedupKey: "gas-fetch-failed");
                ScheduleGasBackoff(false);
                return;
            }

            // Apply gas energy tax (Energiebelasting) and VAT (BTW) from the Taxes table
            // to convert the TTF market price to the all-in consumer price.
            double? allInPrice = await _calculationService.CalculateGasPriceAsync(marketPrice);

            CurrentGasPriceEurPerM3 = allInPrice ?? marketPrice;

            _logger!.LogInformation(
                $"Gas price fetched from Enever.nl: market={marketPrice:F4} EUR/m³, " +
                $"all-in={CurrentGasPriceEurPerM3:F4} EUR/m³ (TTF EGSI + taxes)");

            await _notificationService.ClearByKeyAsync("gas-fetch-failed");
            _gasFailureCount = 0;
            _nextGasAttempt = null;
        }

        /// <summary>
        /// Schedules the next gas fetch after a failure. Exponential backoff (15 min doubling, capped
        /// at 6 h); a token-limit waits until the 1st of next month, when Enever resets the limit.
        /// </summary>
        private void ScheduleGasBackoff(bool tokenLimit)
        {
            _gasFailureCount++;

            var now = _timeZoneService.Now;

            if (tokenLimit)
            {
                _nextGasAttempt = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            }
            else
            {
                var minutes = Math.Min(15 * Math.Pow(2, _gasFailureCount - 1), 360);
                _nextGasAttempt = now.AddMinutes(minutes);
            }

            _logger!.LogInformation($"Gas price fetch backing off until {_nextGasAttempt:dd-MM-yyyy HH:mm}.");
        }
    }
}
