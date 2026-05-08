using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Calculates expected EPEX prices per quarter-hour of the day based on
    /// historical data. Used to fill in tomorrow's prices when they are not
    /// yet available from the day-ahead market (published around 13:00 CET).
    ///
    /// This prevents the MILP planner from discharging the battery into the
    /// evening without knowing that cheap charging may be available tomorrow.
    /// </summary>
    public class ExpectedPriceService
    {
        private readonly EPEXPricesDataService _epexPricesDataService;
        private readonly TimeZoneService _timeZoneService;

        // Number of days to look back for historical average.
        private const int LookbackDays = 60;

        // Minimum number of samples required to use historical average.
        // If fewer samples are available, the fallback price is used.
        private const int MinSamples = 5;

        // Fallback price (EUR/Wh) used when insufficient historical data is available.
        // Corresponds to roughly 0.11 EUR/kWh — a conservative mid-range estimate.
        private const double FallbackPriceEurPerWh = 0.00011;

        public ExpectedPriceService(EPEXPricesDataService epexPricesDataService,
                                    TimeZoneService timeZoneService)
        {
            _epexPricesDataService = epexPricesDataService;
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// Returns a dictionary mapping each quarter-hour index (0..95) of the day
        /// to the average historical EPEX price in EUR/Wh.
        ///
        /// Quarter index 0 = 00:00, 1 = 00:15, ..., 95 = 23:45.
        /// Only complete days (not today) are included to avoid partial day bias.
        /// </summary>
        public async Task<Dictionary<int, double>> GetAveragePricePerQuarterAsync()
        {
            var now = _timeZoneService.Now;
            var start = now.AddDays(-LookbackDays).Date;
            var end = now.Date; // Exclude today to avoid partial data.

            var prices = await _epexPricesDataService.GetList(async (set) =>
            {
                var result = set
                    .Where(p => p.Time >= start && p.Time < end && p.Price.HasValue)
                    .ToList();

                return await Task.FromResult(result);
            });

            // Group by quarter-hour index (0..95) and compute the average price.
            var grouped = prices
                .GroupBy(p => p.Time.Hour * 4 + p.Time.Minute / 15)
                .ToDictionary(
                    g => g.Key,
                    g => g.Count() >= MinSamples
                        ? g.Average(p => p.Price!.Value)
                        : FallbackPriceEurPerWh
                );

            // Ensure all 96 quarter-hours are present.
            for (int i = 0; i < 96; i++)
            {
                if (!grouped.ContainsKey(i))
                    grouped[i] = FallbackPriceEurPerWh;
            }

            return grouped;
        }

        /// <summary>
        /// Generates a list of EPEXPrices entries for the given date based on
        /// historical averages. The entries are NOT stored in the database —
        /// they are only used for planning until real prices become available.
        /// </summary>
        public async Task<List<EPEXPrices>> GetExpectedPricesForDateAsync(DateTime date)
        {
            var averages = await GetAveragePricePerQuarterAsync();

            var result = new List<EPEXPrices>();

            for (int i = 0; i < 96; i++)
            {
                // Use date.Date to strip any time component, then add quarter offset.
                var time = date.Date.AddMinutes(i * 15);

                result.Add(new EPEXPrices
                {
                    Time = time,
                    Price = averages[i]
                });
            }

            return result;
        }
    }
}