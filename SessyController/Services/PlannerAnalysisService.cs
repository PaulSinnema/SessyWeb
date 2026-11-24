using SessyController.Services.Items;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using SessyCommon;

using static SessyCommon.ServiceText;

namespace SessyController.Services
{
    /// <summary>
    /// Read-only analysis of the current stored plan (PlannedQuarter), for the developer
    /// "Planner analysis" page. It explains per quarter why a mode was chosen and flags obvious
    /// improvements, all derived from the stored plan plus the current settings. Heuristic, not a
    /// re-run of the planner.
    /// </summary>
    public class PlannerAnalysisService
    {
        private readonly PlannedQuarterDataService _plannedService;
        private readonly SettingsService _settingsService;
        private readonly BatteryContainer _batteryContainer;
        private readonly TaxesDataService _taxesService;
        private readonly TimeZoneService _timeZoneService;
        private readonly ChargeCostBasisService _costBasisService;

        public PlannerAnalysisService(
            PlannedQuarterDataService plannedService,
            SettingsService settingsService,
            BatteryContainer batteryContainer,
            TaxesDataService taxesService,
            TimeZoneService timeZoneService,
            ChargeCostBasisService costBasisService)
        {
            _costBasisService = costBasisService;
            _plannedService = plannedService;
            _settingsService = settingsService;
            _batteryContainer = batteryContainer;
            _taxesService = taxesService;
            _timeZoneService = timeZoneService;
        }

        // Price spread (EUR/kWh) above which a later quarter counts as a "cheaper now / dearer later".
        private const double SpreadFlagEur = 0.03;

        public async Task<PlannerAnalysis> BuildAsync()
        {
            var now = _timeZoneService.Now.DateFloorQuarter();

            var planned = await _plannedService.GetList(async set =>
                await Task.FromResult(set.Where(p => p.Time >= now)
                                         .OrderBy(p => p.Time).ToList()));

            if (planned.Count == 0)
                return new PlannerAnalysis { HasPlan = false };

            double capacityWh = _batteryContainer.GetTotalCapacity();

            var buys = planned.Select(p => p.BuyingPriceEurKWh).ToList();
            double medianBuy = Stats.Median(buys);
            double maxBuy = buys.Max();
            double minBuy = buys.Min();
            double p75Buy = Stats.Percentile(buys.OrderBy(v => v).ToList(), 75);

            // Highest buy price still to come, per quarter — "is a dearer hour ahead?".
            int n = planned.Count;
            var maxBuyAhead = new double[n];
            double running = double.MinValue;
            for (int i = n - 1; i >= 0; i--)
            {
                maxBuyAhead[i] = running;                    // strictly later quarters only
                running = Math.Max(running, planned[i].BuyingPriceEurKWh);
            }

            // Best value any strictly-later quarter can give stored energy (avoid a future import
            // = buy, or export later = sell). Drives the store-vs-export motivation below.
            var bestFutureAhead = new double[n];
            double runBest = double.MinValue;
            for (int i = n - 1; i >= 0; i--)
            {
                bestFutureAhead[i] = runBest;
                runBest = Math.Max(runBest, Math.Max(planned[i].BuyingPriceEurKWh, planned[i].SellingPriceEurKWh));
            }

            var cfg = _settingsService.Current;
            double roundTrip = cfg.RoundTripEfficiencyFallbackPct > 0 ? cfg.RoundTripEfficiencyFallbackPct / 100.0 : 0.90;
            double cycleCostEff = _settingsService.CycleCost * (cfg.Strategy == OptimizationStrategy.Balanced ? 1.5 : 1.0);

            var quarters = new List<PlannerQuarterAnalysis>(n);
            var spreadByTime = FifoSpreads(planned, _costBasisService.LastProjection?.Consumptions);

            for (int i = 0; i < n; i++)
            {
                var p = planned[i];
                var q = Analyse(p, capacityWh, medianBuy, p75Buy, maxBuyAhead[i], bestFutureAhead[i], roundTrip, cycleCostEff,
                    spreadByTime.TryGetValue(p.Time, out var spread) ? spread : null);
                quarters.Add(q);
            }

            double chargeKWh = Units.QuarterKWh(planned.Sum(p => p.PlannedChargePowerW));
            double dischargeKWh = Units.QuarterKWh(planned.Sum(p => p.PlannedDischargePowerW));
            int remarkCount = quarters.Sum(q => q.Remarks.Count);

            var totals = new PlanTotals
            {
                ChargeKWh = chargeKWh,
                DischargeKWh = dischargeKWh,
                ChargingQuarters = quarters.Count(q => q.Mode == "Charging"),
                DischargingQuarters = quarters.Count(q => q.Mode == "Discharging"),
                ZeroNetHomeQuarters = quarters.Count(q => q.Mode == "ZeroNetHome"),
                DisabledQuarters = quarters.Count(q => q.Mode == "Disabled"),
                HoldReserveQuarters = quarters.Count(q => q.Mode == "HoldReserve"),
                MinBuyEur = minBuy,
                MaxBuyEur = maxBuy,
                MedianBuyEur = medianBuy,
                SpreadEur = maxBuy - minBuy,
                RemarkCount = remarkCount
            };

            return new PlannerAnalysis
            {
                HasPlan = true,
                PeriodStart = planned[0].Time,
                PeriodEnd = planned[^1].Time,
                CapacityKWh = capacityWh / 1000.0,
                Description = BuildDescription(totals, planned.Count),
                Totals = totals,
                Quarters = quarters,
                Parameters = BuildParameters(capacityWh, await CurrentTaxesAsync(now))
            };
        }

        private PlannerQuarterAnalysis Analyse(
            PlannedQuarter p, double capacityWh, double medianBuy, double p75Buy, double maxBuyAhead,
            double bestFutureAhead, double roundTrip, double cycleCostEff, double? spreadEur)
        {
            double socPct = capacityWh > 0 ? p.PlannedChargeLeftWh / capacityWh * 100.0 : 0.0;
            bool charging = p.PlannedChargePowerW > 1.0;
            bool discharging = p.PlannedDischargePowerW > 1.0;
            bool surplus = p.NetLoadWh < 0.0;
            bool aboveReserve = p.PlannedChargeLeftWh > p.MinSocWh + 50.0;
            bool roomToCharge = p.PlannedChargeLeftWh < capacityWh - 100.0;
            bool dearerLater = maxBuyAhead > p.BuyingPriceEurKWh + SpreadFlagEur;

            string reason = p.PlannedMode switch
            {
                "Charging" => surplus
                    ? Tx("Charging with solar surplus (storing own generation).")
                    : Tx("Charging from the grid: cheap quarter, saved for a dearer later moment."),
                "Discharging" => Tx("Discharging to the grid: price high enough to sell."),
                "ZeroNetHome" when discharging =>
                    Tx("Self-consumption from the battery: household deficit covered, SOC above the reserve."),
                "ZeroNetHome" =>
                    Tx("Battery idle (no deficit to cover, or at the reserve floor)."),
                "HoldReserve" =>
                    Tx("Battery holds its energy (reserve reached, or the plan keeps it for later): stores solar surplus when there is any, never discharges; the house imports otherwise."),
                "Disabled" when surplus =>
                    Tx("Off: solar surplus goes to the grid (storing does not pay here)."),
                "Disabled" =>
                    Tx("Off: no trade planned for this quarter."),
                _ => p.PlannedMode
            };

            var remarks = new List<string>();

            // The greedy flaw we measured: spending the battery cheap while a dearer hour is ahead.
            if (discharging && p.BuyingPriceEurKWh < medianBuy && dearerLater)
            {
                remarks.Add(Tf("Discharges at a low price (€{0:F3}) while a dearer quarter is ahead (€{1:F3}). Saving energy for the peak may earn more.", p.BuyingPriceEurKWh, maxBuyAhead));
            }

            // Solar surplus exported instead of stored, while a dearer hour is ahead and there is room.
            if (p.PlannedMode == "Disabled" && surplus && roomToCharge && dearerLater)
            {
                remarks.Add(Tf("Solar surplus not stored while a dearer quarter is ahead (€{0:F3}) and there is SOC headroom.", maxBuyAhead));
            }

            // Idle in an expensive quarter with SOC available above the reserve.
            if (p.PlannedMode == "ZeroNetHome" && !discharging && !charging
                && p.BuyingPriceEurKWh >= p75Buy && aboveReserve)
            {
                remarks.Add(Tf("Battery idle in an expensive quarter (€{0:F3}) while SOC is available.", p.BuyingPriceEurKWh));
            }

            // Charging at a relatively expensive price.
            if (charging && !surplus && p.BuyingPriceEurKWh > medianBuy)
            {
                remarks.Add(Tf("Charges from the grid at a price above the median (€{0:F3} > €{1:F3}).", p.BuyingPriceEurKWh, medianBuy));
            }

            // Exporting below the median price.
            if (p.PlannedMode == "Discharging" && p.SellingPriceEurKWh < medianBuy)
            {
                remarks.Add(Tf("Discharges to the grid below the median buy price (sell €{0:F3}).", p.SellingPriceEurKWh));
            }

            // Why solar is (not) exported — the motivation users ask about in the morning.
            string solarExportNote = string.Empty;
            if (p.SolarForecastW > 1.0)
            {
                double bestFuture = bestFutureAhead > double.MinValue ? bestFutureAhead : 0.0;
                double exportValue = p.SellingPriceEurKWh;
                double storeValue = roundTrip * (bestFuture - cycleCostEff);

                if (!surplus)
                {
                    solarExportNote = Tx("Solar is used directly for the household load; there is no surplus to export this quarter.");
                }
                else if (p.PlannedMode == "Disabled")
                {
                    solarExportNote = Tf("Solar surplus is exported now: selling at €{0:F3} beats keeping it (store value €{1:F3} ≈ {2:F0}% round-trip × best later price €{3:F3}, minus wear €{4:F3}).", exportValue, storeValue, roundTrip * 100.0, bestFuture, cycleCostEff);
                }
                else if (storeValue >= exportValue)
                {
                    solarExportNote = Tf("Solar surplus is stored / self-consumed, not exported, because a later moment pays more: storing is worth €{0:F3} ({1:F0}% round-trip × best later price €{2:F3}) versus exporting now at €{3:F3} — so keeping it wins.", storeValue, roundTrip * 100.0, bestFuture, exportValue);
                }
                else
                {
                    solarExportNote = Tf("Solar surplus is kept, but exporting now (€{0:F3}) would beat storing it (€{1:F3}); no dearer moment is ahead, so exporting could be better here.", exportValue, storeValue);
                    remarks.Add(Tf("Solar surplus stored while exporting now (€{0:F3}) would pay more than keeping it (€{1:F3}).", exportValue, storeValue));
                }
            }

            string pricePosition = p.BuyingPriceEurKWh <= medianBuy * 0.95 ? "Cheap"
                                 : p.BuyingPriceEurKWh >= p75Buy ? "Expensive"
                                 : "Average";

            return new PlannerQuarterAnalysis
            {
                Time = p.Time,
                Mode = p.PlannedMode,
                ChargePowerW = p.PlannedChargePowerW,
                DischargePowerW = p.PlannedDischargePowerW,
                PlannedPowerW = p.PlannedPowerW,
                UnthrottledPowerW = p.PlannedUnthrottledPowerW,
                SocWh = p.PlannedChargeLeftWh,
                SocPct = socPct,
                BuyPrice = p.BuyingPriceEurKWh,
                SellPrice = p.SellingPriceEurKWh,
                SolarForecastWh = p.SolarForecastW,
                ConsumptionForecastW = p.ConsumptionForecastW,
                NetLoadWh = p.NetLoadWh,
                MinSocWh = p.MinSocWh,
                CostBasisEur = p.ProjectedCostBasisEurKWh,
                PricePosition = pricePosition,
                MaxBuyAheadEur = maxBuyAhead > double.MinValue ? maxBuyAhead : 0.0,
                SpreadEur = spreadEur,
                Reason = reason,
                SolarExportNote = solarExportNote,
                Remarks = remarks
            };
        }

        /// <summary>
        /// FIFO spread per quarter from the cost-basis projection: every pop pairs a sale with
        /// the layer that fed it. Margin = sale value − layer cost per delivered kWh, weighted by
        /// Wh, credited to both the selling and the charging quarter.
        /// </summary>
        internal static Dictionary<DateTime, double> FifoSpreads(
            IReadOnlyList<PlannedQuarter> planned,
            IReadOnlyList<ChargeCostBasisService.LayerConsumption>? consumptions)
        {
            var result = new Dictionary<DateTime, double>();
            if (consumptions == null || consumptions.Count == 0) return result;

            var byTime = planned.FirstPer(p => p.Time);
            var sums = new Dictionary<DateTime, (double Wh, double MarginWh)>();

            void Add(DateTime t, double wh, double margin)
            {
                var s = sums.GetValueOrDefault(t);
                sums[t] = (s.Wh + wh, s.MarginWh + wh * margin);
            }

            foreach (var c in consumptions)
            {
                if (!byTime.TryGetValue(c.SoldAt, out var sold)) continue;

                double margin = DeliveryValue(sold) - c.CostEurPerKWhDelivered;
                Add(c.SoldAt, c.StoredWh, margin);
                if (c.ChargedAt.HasValue) Add(c.ChargedAt.Value, c.StoredWh, margin);
            }

            foreach (var (t, s) in sums)
                if (s.Wh > 1.0) result[t] = s.MarginWh / s.Wh;

            return result;
        }

        /// <summary>Value per delivered kWh: avoided buy for the house deficit, sell price for the export.</summary>
        private static double DeliveryValue(PlannedQuarter p)
        {
            double deliveredWh = p.PlannedDischargePowerW * 0.25;
            if (deliveredWh <= 1.0) return p.BuyingPriceEurKWh;

            double houseWh = Math.Min(Math.Max(p.NetLoadWh, 0.0), deliveredWh);
            return (houseWh * p.BuyingPriceEurKWh + (deliveredWh - houseWh) * p.SellingPriceEurKWh) / deliveredWh;
        }

        private static string BuildDescription(PlanTotals t, int count)
        {
            string trade = (t.ChargingQuarters + t.DischargingQuarters) == 0
                ? Tx("The plan does not actively trade on the grid (no charge or sell quarters); it mainly covers self-consumption.")
                : Tf("The plan charges in {0} and discharges to the grid in {1} quarters.", t.ChargingQuarters, t.DischargingQuarters);

            return Tf("Plan over {0} quarters. {1} Total charged {2:F1} kWh, discharged {3:F1} kWh. Buy price ranges from €{4:F3} to €{5:F3} (median €{6:F3}, spread €{7:F3}). Self-consumption in {8} quarters, off in {9}. ", count, trade, t.ChargeKWh, t.DischargeKWh, t.MinBuyEur, t.MaxBuyEur, t.MedianBuyEur, t.SpreadEur, t.ZeroNetHomeQuarters, t.DisabledQuarters) +
                   (t.RemarkCount > 0
                        ? Tf("{0} improvement point(s) flagged.", t.RemarkCount)
                        : Tx("No improvement points flagged."));
        }

        private async Task<Taxes?> CurrentTaxesAsync(DateTime now)
        {
            var taxes = await _taxesService.GetList(async set =>
                await Task.FromResult(set.Where(t => t.Time <= now).OrderByDescending(t => t.Time).ToList()));
            return taxes.FirstOrDefault();
        }

        private List<PlannerParam> BuildParameters(double capacityWh, Taxes? taxes)
        {
            var s = _settingsService.Current;
            double derivedCycle = _settingsService.CycleCost;
            bool balanced = s.Strategy == OptimizationStrategy.Balanced;

            var list = new List<PlannerParam>
            {
                new("Strategy", Tx("Optimisation strategy"), s.Strategy.ToString()),
                new("Strategy", Tx("Cycle cost source"), s.UseCalculatedCycleCost ? Tx("Derived from investments") : "Fixed"),
                new("Strategy", Tx("Cycle cost (derived/fixed)"), Tf("€ {0:F4}/kWh", derivedCycle)),
                new("Strategy", Tx("Cycle cost effective"), balanced ? Tf("€ {0:F4}/kWh (Balanced ×1.5)", derivedCycle * 1.5) : Tf("€ {0:F4}/kWh", derivedCycle)),

                new("Reserve", Tx("Minimum reserve (%)"), $"{s.FixedNightReservePct:F1}"),

                new("Arbitrage", Tx("Future value discount (%/hour)"), $"{s.FutureValueDiscountPerHour * 100.0:F2}"),
                new("Arbitrage", Tx("Predicted price mode"), s.PredictedPriceMode.ToString()),
                new("Arbitrage", Tx("Predicted price risk margin"), Tf("€ {0:F3}/kWh", s.PredictedPriceRiskMarginEur)),
                new("Arbitrage", Tx("Planning horizon (hours)"), s.PlanningHorizonHours == 0 ? Tx("0 (no limit)") : s.PlanningHorizonHours.ToString()),
                new("Arbitrage", "Carry-forward", s.CarryForwardEnabled ? "On" : "Off"),
                new("Arbitrage", Tx("Shift discharge"), s.ShiftDischargeEnabled ? "On" : "Off"),
                new("Arbitrage", Tx("Replacement cost window (days)"), s.ReplacementCostWindowDays.ToString()),
                new("Arbitrage", Tx("Replacement cost percentile"), $"{s.ReplacementCostPercentile:F0}"),

                new("Battery", Tx("Control method"), s.BatteryControlMethod == BatteryControlMethod.BatterySetpoint ? Tx("Setpoint per battery") : Tx("P1 grid target")),
                new("Battery", "Capacity", $"{capacityWh / 1000.0:F2} kWh"),
                new("Battery", Tx("Round-trip efficiency fallback (%)"), $"{s.RoundTripEfficiencyFallbackPct:F0}"),
                new("Battery", Tx("Throttle fallback (%)"), $"{s.ThrottleFallbackPct:F0}"),
                new("Battery", Tx("Annual solar production"), $"{s.SolarAnnualProductionKWh:F0} kWh"),

                new("Learning", "Self-learning", s.SelfLearningEnabled ? "On" : "Off"),
                new("Learning", Tx("Last learned"), s.LastLearnedAt?.ToString("dd-MM-yyyy HH:mm") ?? "never"),
                new("Learning", "Summary", string.IsNullOrWhiteSpace(s.LastLearnedSummary) ? "—" : s.LastLearnedSummary!),
            };

            if (taxes != null)
            {
                list.Add(new("Taxes", Tx("Valid from"), taxes.Time?.ToString("dd-MM-yyyy") ?? "—"));
                list.Add(new("Taxes", "Netting", taxes.Netting ? "On" : "Off"));
                list.Add(new("Taxes", Tx("Energy tax"), Tf("€ {0:F5}/kWh", taxes.EnergyTax)));
                list.Add(new("Taxes", "VAT", $"{taxes.ValueAddedTax:F0}%"));
                list.Add(new("Taxes", Tx("Purchase compensation"), Tf("€ {0:F5}/kWh", taxes.PurchaseCompensation)));
                list.Add(new("Taxes", Tx("Return delivery costs"), Tf("€ {0:F5}/kWh", taxes.ReturnDeliveryCompensation)));
            }

            return list;
        }

        // ── small stats helpers ────────────────────────────────────────────

    }

    public sealed class PlannerAnalysis
    {
        public bool HasPlan { get; init; }
        public DateTime? PeriodStart { get; init; }
        public DateTime? PeriodEnd { get; init; }
        public double CapacityKWh { get; init; }
        public string Description { get; init; } = string.Empty;
        public PlanTotals Totals { get; init; } = new();
        public List<PlannerQuarterAnalysis> Quarters { get; init; } = new();
        public List<PlannerParam> Parameters { get; init; } = new();
    }

    public sealed class PlanTotals
    {
        public double ChargeKWh { get; init; }
        public double DischargeKWh { get; init; }
        public int ChargingQuarters { get; init; }
        public int DischargingQuarters { get; init; }
        public int ZeroNetHomeQuarters { get; init; }
        public int DisabledQuarters { get; init; }
        public int HoldReserveQuarters { get; init; }
        public double MinBuyEur { get; init; }
        public double MaxBuyEur { get; init; }
        public double MedianBuyEur { get; init; }
        public double SpreadEur { get; init; }
        public int RemarkCount { get; init; }
    }

    public sealed class PlannerQuarterAnalysis
    {
        public DateTime Time { get; init; }
        public string Mode { get; init; } = string.Empty;
        public double ChargePowerW { get; init; }
        public double DischargePowerW { get; init; }
        public double PlannedPowerW { get; init; }
        public double UnthrottledPowerW { get; init; }
        public double SocWh { get; init; }
        public double SocPct { get; init; }
        public double BuyPrice { get; init; }
        public double SellPrice { get; init; }
        public double SolarForecastWh { get; init; }
        public double ConsumptionForecastW { get; init; }
        public double NetLoadWh { get; init; }
        public double MinSocWh { get; init; }
        public double CostBasisEur { get; init; }
        public string PricePosition { get; init; } = string.Empty;
        public double MaxBuyAheadEur { get; init; }

        /// <summary>
        /// Margin per delivered kWh of the energy this quarter sells or buys, paired by FIFO:
        /// sale value minus the cost of the layers it consumes. Null when nothing is paired
        /// within the plan (no (dis)charge, or energy carried past the horizon).
        /// </summary>
        public double? SpreadEur { get; init; }
        public string Reason { get; init; } = string.Empty;
        public string SolarExportNote { get; init; } = string.Empty;
        public List<string> Remarks { get; init; } = new();
    }

    public sealed record PlannerParam(string Group, string Name, string Value);
}
