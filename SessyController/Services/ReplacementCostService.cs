using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// What it will cost to put one kWh back into the battery later — the replacement cost.
    ///
    /// The planner needs this for two decisions that are really the same decision:
    ///
    ///   floor          A kWh already in the battery should not be sold below what buying it
    ///                  back would cost. Until now this used the FIFO cost basis, which is what
    ///                  was *paid* — sunk, and in autumn/winter a poor estimate of what the next
    ///                  kWh will cost.
    ///   terminal value Energy that survives the end of the horizon is worth EUR 0 in the
    ///                  objective, so the planner can hold stock but can never *acquire* stock
    ///                  for beyond the horizon: Candidate B needs a discharge quarter inside it.
    ///                  Pricing carry-forward at the replacement cost removes that blind spot —
    ///                  which is exactly where charging at a negative price and holding pays,
    ///                  and negative prices become far more common once netting (saldering) ends.
    ///
    /// Measured, not configured: the cheapest all-in buy price of each day over a trailing
    /// window, then a low percentile of those daily minima. Low on purpose — a terminal value
    /// set too high makes charging always attractive and selling never, and the battery ends up
    /// full and idle. The percentile says "on most days I can buy at least this cheaply", so
    /// carrying energy forward only wins when today is genuinely cheaper than a normal day.
    ///
    /// A second, measured guard caps the result at the median all-in buy price of the same
    /// window: carry-forward must never be worth more than what energy normally costs.
    ///
    /// The all-in price (tax, VAT, compensation) is a monotone transform of the market price
    /// within a day, so the cheapest quarter can be picked on the raw price and converted after.
    /// That keeps the tax calculation to two quarters per day instead of ninety-six.
    /// </summary>
    public class ReplacementCostService
    {
        private readonly EPEXPricesDataService _epexPricesDataService;
        private readonly ICalculationService _calculationService;
        private readonly SettingsService _settingsService;
        private readonly TimeZoneService _timeZoneService;
        private readonly LoggingService<ReplacementCostService> _logger;

        /// <summary>Days with fewer quarters than this are partial and would skew the minimum.</summary>
        private const int MinQuartersPerDay = 80;

        /// <summary>Below this many usable days the estimate is not trusted at all.</summary>
        private const int MinDays = 7;

        private const int DefaultWindowDays = 30;
        private const double DefaultPercentile = 25.0;

        /// <summary>Recomputed at most this often; a trailing window of days barely moves per quarter.</summary>
        private static readonly TimeSpan CacheTime = TimeSpan.FromHours(6);

        private ReplacementCostSnapshot? _cached;
        private DateTime _cachedAt = DateTime.MinValue;

        public ReplacementCostService(
            EPEXPricesDataService epexPricesDataService,
            ICalculationService calculationService,
            SettingsService settingsService,
            TimeZoneService timeZoneService,
            LoggingService<ReplacementCostService> logger)
        {
            _epexPricesDataService = epexPricesDataService;
            _calculationService = calculationService;
            _settingsService = settingsService;
            _timeZoneService = timeZoneService;
            _logger = logger;
        }

        /// <summary>
        /// Replacement cost with the numbers it was derived from, for logging and the UI.
        /// EurPerKWh is 0 when there is not enough history — the planner then keeps its old
        /// behaviour instead of trading on a made-up value.
        /// </summary>
        public sealed record ReplacementCostSnapshot(
            double EurPerKWh,
            int Days,
            int WindowDays,
            double Percentile,
            double MedianBuyEurPerKWh,
            bool Capped);

        /// <summary>Replacement cost in EUR/kWh, or 0 when it cannot be measured yet.</summary>
        public async Task<double> GetReplacementCostAsync()
            => (await GetSnapshotAsync().ConfigureAwait(false)).EurPerKWh;

        public async Task<ReplacementCostSnapshot> GetSnapshotAsync()
        {
            var now = _timeZoneService.Now;

            if (_cached != null && now - _cachedAt < CacheTime)
                return _cached;

            var snapshot = await ComputeAsync(now).ConfigureAwait(false);

            _cached = snapshot;
            _cachedAt = now;

            if (snapshot.EurPerKWh > 0.0)
                _logger.LogInformation(
                    $"Replacement cost {snapshot.EurPerKWh:F4} EUR/kWh " +
                    $"(P{snapshot.Percentile:F0} of the daily cheapest price over {snapshot.Days} of " +
                    $"{snapshot.WindowDays} days, median buy {snapshot.MedianBuyEurPerKWh:F4}" +
                    (snapshot.Capped ? ", capped at the median)." : ")."));

            return snapshot;
        }

        /// <summary>Drops the cache so a settings change takes effect on the next plan.</summary>
        public void Invalidate() => _cached = null;

        private async Task<ReplacementCostSnapshot> ComputeAsync(DateTime now)
        {
            var settings = _settingsService.Current;

            int windowDays = settings.ReplacementCostWindowDays > 0
                ? settings.ReplacementCostWindowDays
                : DefaultWindowDays;

            double percentile = settings.ReplacementCostPercentile > 0.0
                ? Math.Clamp(settings.ReplacementCostPercentile, 1.0, 99.0)
                : DefaultPercentile;

            var empty = new ReplacementCostSnapshot(0.0, 0, windowDays, percentile, 0.0, false);

            var start = now.AddDays(-windowDays);

            var prices = await _epexPricesDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(p => p.Time >= start && p.Time <= now && p.Price != null)
                    .ToList())).ConfigureAwait(false);

            if (prices == null || prices.Count == 0) return empty;

            // One cheapest and one median quarter per complete day. Partial days are skipped:
            // half a day of prices has an unrepresentative minimum.
            var cheapestPerDay = new List<DateTime>();
            var medianPerDay = new List<DateTime>();

            foreach (var day in prices.GroupBy(p => p.Time.Date))
            {
                var ordered = day.OrderBy(p => p.Price!.Value).ToList();
                if (ordered.Count < MinQuartersPerDay) continue;

                cheapestPerDay.Add(ordered[0].Time);
                medianPerDay.Add(ordered[ordered.Count / 2].Time);
            }

            if (cheapestPerDay.Count < MinDays) return empty;

            var allIn = await _calculationService
                .CalculateEnergyPricesBatchAsync(cheapestPerDay.Concat(medianPerDay))
                .ConfigureAwait(false);

            var dailyMinima = cheapestPerDay
                .Where(t => allIn.ContainsKey(t))
                .Select(t => allIn[t].Buying)
                .OrderBy(v => v)
                .ToList();

            var dailyMedians = medianPerDay
                .Where(t => allIn.ContainsKey(t))
                .Select(t => allIn[t].Buying)
                .OrderBy(v => v)
                .ToList();

            if (dailyMinima.Count < MinDays) return empty;

            double value = Percentiles.Percentile(dailyMinima, percentile);
            double medianBuy = dailyMedians.Count > 0 ? Percentiles.Percentile(dailyMedians, 50.0) : double.MaxValue;

            // Negative days pull the percentile below zero. Carry-forward is worth nothing then,
            // and a negative floor would let the planner sell at any price at all.
            if (value < 0.0) value = 0.0;

            bool capped = value > medianBuy;
            if (capped) value = medianBuy;

            return new ReplacementCostSnapshot(
                value,
                dailyMinima.Count,
                windowDays,
                percentile,
                dailyMedians.Count > 0 ? medianBuy : 0.0,
                capped);
        }
    }
}
