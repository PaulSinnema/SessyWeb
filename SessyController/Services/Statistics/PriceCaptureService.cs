using SessyController.Services.Items;
using SessyCommon.Extensions;

namespace SessyController.Services.Statistics
{
    /// <summary>
    /// One month of battery trading: the price spread the battery achieved against the best spread the
    /// day's prices offered for the same number of charge and discharge quarters (EUR/kWh).
    /// </summary>
    public sealed record PriceCaptureMonth(DateTime Month, double CapturedEurPerKWh, double BestEurPerKWh, double DischargedKWh, int Days)
    {
        public string Label => Month.ToString("MMM yyyy");

        public double? CapturePercent => BestEurPerKWh > 0.0 ? CapturedEurPerKWh / BestEurPerKWh * 100.0 : null;
    }

    public sealed record PriceCaptureDay(DateTime Date, double CapturedEurPerKWh, double BestEurPerKWh, double DischargedKWh);

    /// <summary>How much of the daily price spread the battery actually captures.</summary>
    public class PriceCaptureService
    {
        /// <summary>Days with less discharge than this say nothing about timing.</summary>
        private const double MinDischargedKWh = 0.5;

        private readonly QuarterlyFactsService _quarterlyFactsService;

        public PriceCaptureService(QuarterlyFactsService quarterlyFactsService)
        {
            _quarterlyFactsService = quarterlyFactsService;
        }

        public async Task<List<PriceCaptureMonth>> GetAsync(DateTime from, DateTime to)
        {
            var quarters = await _quarterlyFactsService.GetAsync(from, to);

            return Months(quarters.GroupBy(q => q.Time.Date).Select(g => Day(g.Key, g.ToList())).OfType<PriceCaptureDay>());
        }

        /// <summary>
        /// Captured: average buy price of the discharged energy minus that of the charged energy.
        /// Best: the same kWh in the most expensive quarters minus the cheapest, at most the day's largest quarter each.
        /// </summary>
        public static PriceCaptureDay? Day(DateTime date, IReadOnlyList<MeasurementView> quarters)
        {
            var priced = quarters.Where(q => q.BuyingPriceEur != 0.0).ToList();
            var charged = priced.Where(q => q.BatteryChargedKWh > 0.0).ToList();
            var discharged = priced.Where(q => q.BatteryDischargedKWh > 0.0).ToList();

            var chargedKWh = charged.Sum(q => q.BatteryChargedKWh);
            var dischargedKWh = discharged.Sum(q => q.BatteryDischargedKWh);

            if (chargedKWh <= 0.0 || dischargedKWh < MinDischargedKWh)
                return null;

            var captured = discharged.Sum(q => q.BatteryDischargedKWh * q.BuyingPriceEur) / dischargedKWh
                         - charged.Sum(q => q.BatteryChargedKWh * q.BuyingPriceEur) / chargedKWh;

            // Same kWh, each quarter capped at the day's largest measured quarter, so best bounds captured.
            var prices = priced.Select(q => q.BuyingPriceEur).OrderBy(p => p).ToList();
            var best = FillAverage(Enumerable.Reverse(prices), dischargedKWh, discharged.Max(q => q.BatteryDischargedKWh))
                     - FillAverage(prices, chargedKWh, charged.Max(q => q.BatteryChargedKWh));

            return new PriceCaptureDay(date, captured, Math.Max(0.0, best), dischargedKWh);
        }

        /// <summary>Average price of kWh spread over the quarters in the given order, at most capKWh per quarter.</summary>
        private static double FillAverage(IEnumerable<double> prices, double kWh, double capKWh)
        {
            var remaining = kWh;
            var sum = 0.0;

            foreach (var price in prices)
            {
                var take = Math.Min(capKWh, remaining);
                sum += take * price;
                remaining -= take;

                if (remaining <= 1e-9) break;
            }

            return sum / kWh;
        }

        /// <summary>Discharge-weighted monthly averages.</summary>
        public static List<PriceCaptureMonth> Months(IEnumerable<PriceCaptureDay> days) =>
            days
                .GroupBy(d => d.Date.MonthStart())
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var kWh = g.Sum(d => d.DischargedKWh);
                    return new PriceCaptureMonth(g.Key,
                        g.Sum(d => d.CapturedEurPerKWh * d.DischargedKWh) / kWh,
                        g.Sum(d => d.BestEurPerKWh * d.DischargedKWh) / kWh,
                        kWh, g.Count());
                })
                .ToList();
    }
}
