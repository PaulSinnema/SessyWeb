using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using SessyCommon.Extensions;

namespace SessyController.Services
{
    /// <summary>One measured interval: grid import/export (kWh), its EPEX price (EUR/kWh) and whether it was daylight (Zonnebonus).</summary>
    public readonly record struct GridFlow(DateTime Time, double ImportKWh, double ExportKWh, double MarketPrice, bool IsDaylight = false);

    /// <summary>Yearly electricity bill broken down per line, all amounts incl. VAT (EUR).</summary>
    public sealed record FinancialForecast(
        string Label,
        DateTime From,
        DateTime To,
        bool Netting,
        double ImportKWh,
        double ExportKWh,
        double ImportCostEur,
        double ExportRevenueEur,
        double EnergyTaxEur,
        double FixedCostsEur,
        double TaxReductionEur)
    {
        public double NetKWh => ImportKWh - ExportKWh;

        public double TotalEur => ImportCostEur - ExportRevenueEur + EnergyTaxEur + FixedCostsEur - TaxReductionEur;
    }

    /// <summary>
    /// Financial forecast for next year: the measured grid flows of the last 12 full months, priced
    /// once with the tariffs that applied and once with next year's tariffs (Taxes row valid on
    /// 1 January). Same usage and same market prices in both, so the difference is the tariff change
    /// (e.g. the end of netting). The planner adapts its behaviour to new tariffs, so the next-year
    /// figure is an upper bound.
    /// </summary>
    public class FinancialForecastService
    {
        private readonly EnergyHistoryDataService _energyHistoryDataService;
        private readonly EPEXPricesDataService _epexPricesDataService;
        private readonly TaxesDataService _taxesDataService;
        private readonly TimeZoneService _timeZoneService;
        private readonly SettingsService _settingsService;

        public FinancialForecastService(
            EnergyHistoryDataService energyHistoryDataService,
            EPEXPricesDataService epexPricesDataService,
            TaxesDataService taxesDataService,
            TimeZoneService timeZoneService,
            SettingsService settingsService)
        {
            _settingsService = settingsService;
            _energyHistoryDataService = energyHistoryDataService;
            _epexPricesDataService = epexPricesDataService;
            _taxesDataService = taxesDataService;
            _timeZoneService = timeZoneService;
        }

        /// <summary>(last 12 months at their own tariffs, same usage at next year's tariffs); null without data.</summary>
        public async Task<(FinancialForecast LastYear, FinancialForecast NextYear)?> GetForecastAsync()
        {
            var now = _timeZoneService.Now;
            var to = now.MonthStart();
            var from = to.AddYears(-1);

            var flows = await LoadFlowsAsync(from, to).ConfigureAwait(false);
            if (flows.Count == 0) return null;

            var taxes = (await _taxesDataService.GetList(async set =>
                    await Task.FromResult(set.Where(t => t.Time != null).ToList())).ConfigureAwait(false))
                .OrderBy(t => t.Time)
                .ToList();
            if (taxes.Count == 0) return null;

            var nextYearStart = new DateTime(now.Year + 1, 1, 1);
            var nextYearTaxes = TaxesAt(taxes, nextYearStart);
            if (nextYearTaxes == null) return null;

            var lastYear = Calculate($"Last 12 months ({from:MM-yyyy} – {to.AddDays(-1):MM-yyyy})",
                from, to, flows, t => TaxesAt(taxes, t));
            var nextYear = Calculate($"{nextYearStart.Year}, same usage",
                nextYearStart, nextYearStart.AddYears(1), flows, _ => nextYearTaxes);

            return (lastYear, nextYear);
        }

        /// <summary>
        /// Prices the flows with the tariffs <paramref name="taxesAt"/> returns per moment. With netting
        /// the energy tax and VAT apply to the net import over the period (not below zero); without it
        /// every imported kWh carries tax and VAT and an exported kWh earns market price plus the (negative)
        /// return compensation. Fixed costs and the tax reduction are pro rata per day. Pure for tests.
        /// </summary>
        internal static FinancialForecast Calculate(
            string label, DateTime from, DateTime to, IReadOnlyList<GridFlow> flows, Func<DateTime, Taxes?> taxesAt)
        {
            double importKWh = 0, exportKWh = 0;
            double importExcl = 0, exportExcl = 0;     // supply part, excl. VAT
            double netTaxBase = 0;                     // netting: energy tax on net kWh, excl. VAT
            double importTaxNoNetting = 0;             // no netting: energy tax on import, excl. VAT
            double vatFactorSum = 0; int vatCount = 0;
            bool anyNetting = false;

            foreach (var f in flows)
            {
                var tx = taxesAt(f.Time);
                if (tx == null) continue;

                double vat = tx.VatFactor;
                vatFactorSum += vat; vatCount++;
                double sellCompensation = EnergyPriceCalculator.SellCompensation(
                    f.MarketPrice, tx.SignedReturnDeliveryCompensation, tx.SolarBonusPercentage, f.IsDaylight);

                importKWh += f.ImportKWh;
                exportKWh += f.ExportKWh;
                importExcl += f.ImportKWh * (f.MarketPrice + tx.PurchaseCompensation);

                if (tx.Netting)
                {
                    anyNetting = true;
                    exportExcl += f.ExportKWh * (f.MarketPrice + sellCompensation);
                    netTaxBase += (f.ImportKWh - f.ExportKWh) * tx.EnergyTax;
                }
                else
                {
                    // Export without netting: no tax, no VAT (see EnergyPriceCalculator).
                    exportExcl += f.ExportKWh * (f.MarketPrice + sellCompensation) / vat;
                    importTaxNoNetting += f.ImportKWh * tx.EnergyTax;
                }
            }

            double avgVat = vatCount > 0 ? vatFactorSum / vatCount : 1.21;

            // Fixed costs and reduction: yearly amounts, pro rata per day at that day's tariffs.
            double fixedExcl = 0, reductionExcl = 0;
            for (var day = from.Date; day < to.Date; day = day.AddDays(1))
            {
                var tx = taxesAt(day);
                if (tx == null) continue;

                double daysInYear = DateTime.IsLeapYear(day.Year) ? 366.0 : 365.0;
                fixedExcl += (tx.NetManagementCost + tx.FixedTransportFee + tx.CapacityTransportFee) / daysInYear;
                reductionExcl += tx.TaxReduction / daysInYear;
            }

            double energyTaxExcl = Math.Max(0.0, netTaxBase) + importTaxNoNetting;

            return new FinancialForecast(
                label, from, to, anyNetting,
                importKWh, exportKWh,
                ImportCostEur: importExcl * avgVat,
                ExportRevenueEur: exportExcl * avgVat,
                EnergyTaxEur: energyTaxExcl * avgVat,
                FixedCostsEur: fixedExcl * avgVat,
                TaxReductionEur: reductionExcl * avgVat);
        }

        internal static Taxes? TaxesAt(IReadOnlyList<Taxes> ordered, DateTime time)
            => ordered.LastOrDefault(t => t.Time <= time) ?? ordered.FirstOrDefault();

        /// <summary>
        /// Grid flows from the P1 meter readings (EnergyHistory), per meter, priced at the EPEX price
        /// of the interval. A gap in the readings is kept as one interval at the average price over it,
        /// so the totals still match the meter.
        /// </summary>
        private async Task<List<GridFlow>> LoadFlowsAsync(DateTime from, DateTime to)
        {
            var history = await _energyHistoryDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(h => h.Time >= from && h.Time <= to)
                    .ToList())).ConfigureAwait(false);

            var prices = (await _epexPricesDataService.GetList(async set =>
                    await Task.FromResult(set
                        .Where(p => p.Time >= from.AddDays(-1) && p.Time <= to && p.Price.HasValue)
                        .ToList())).ConfigureAwait(false))
                .FirstPer(p => p.Time, p => p.Price!.Value);

            var sortedPrices = prices.OrderBy(p => p.Key).ToList();
            var flows = new List<GridFlow>();

            foreach (var interval in QuarterlyFactsService.GridIntervals(history))
            {
                double importKWh = interval.ImportWh / 1000.0;
                double exportKWh = interval.ExportWh / 1000.0;

                double? price = PriceFor(prices, sortedPrices, interval.Start, interval.End);
                if (price.HasValue && (importKWh > 0.0 || exportKWh > 0.0))
                    flows.Add(new GridFlow(interval.Start, importKWh, exportKWh, price.Value, IsDaylight(interval.Start)));
            }

            return flows;
        }

        /// <summary>True between sunrise and sunset at the configured location (Zonnebonus).</summary>
        private bool IsDaylight(DateTime time)
            => _timeZoneService.GetSunlightLevel(time, _settingsService.Current.Latitude, _settingsService.Current.Longitude)
               == SolCalc.Data.SunlightLevel.Daylight;

        /// <summary>Price of the quarter (or hour) at <paramref name="start"/>; over a gap the average price.</summary>
        private static double? PriceFor(Dictionary<DateTime, double> prices, List<KeyValuePair<DateTime, double>> sorted,
            DateTime start, DateTime end)
        {
            if ((end - start).TotalMinutes <= 60.0)
            {
                if (prices.TryGetValue(start, out var p)) return p;
                if (prices.TryGetValue(new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0), out var ph)) return ph;
                return null;
            }

            var span = sorted.Where(x => x.Key >= start && x.Key < end).Select(x => x.Value).ToList();
            return span.Count > 0 ? span.Average() : null;
        }
    }
}
