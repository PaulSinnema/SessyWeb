using SessyWeb.Pages;
using SessyCommon;

using static SessyCommon.ServiceText;

namespace SessyWeb.Services
{
    /// <summary>Why one quarter of the plan looks the way it does.</summary>
    public sealed record QuarterWhy(string Plain, string Technical);

    /// <summary>Why the whole plan looks the way it does: one plain summary plus technical detail.</summary>
    public sealed record PlanWhy(string Plain, IReadOnlyList<string> Technical);

    /// <summary>
    /// Read-only explainer over an already-built plan. It does not touch the planner; it reconstructs
    /// the decisive comparison behind each quarter (and the plan as a whole) from the same numbers the
    /// planner used — prices, planned charge/discharge, solar, consumption and the resulting SOC — so
    /// the UI can answer "why is the plan like this?".
    ///
    /// Because the planner is greedy, every quarter's mode follows from one comparison: charge in the
    /// cheapest hours, sell into the most expensive, cover the house from the battery, and only hold
    /// energy when no trade beats the cycle cost. That is exactly what this puts into words.
    /// </summary>
    /// <remarks>
    /// KEEP IN SYNC WITH THE PLANNER. This mirrors BatteryGreedyPlanner's decision logic — the
    /// baseline export-vs-store threshold, the arbitrage comparisons, the meaning of each
    /// mode/ActionMode (incl. HoldReserve: hold energy, never discharge; ZeroNetHome only where the
    /// house is covered in full), and the price / round-trip / cycle-cost trade-offs. If the planner changes
    /// (a new mode, a different threshold, a changed price build-up), update this explainer too, or
    /// "Why this plan?" will describe behaviour the planner no longer follows.
    /// </remarks>
    public sealed class PlanExplanationService
    {

        private static string Euro(double v) => $"€{v:0.000}/kWh";
        private static double ChargeKWh(QuarterlyInfoView q) => Units.QuarterKWh(q.PlannedChargePowerW);
        private static double DischargeKWh(QuarterlyInfoView q) => Units.QuarterKWh(q.PlannedDischargePowerW);
        // PlannedMode ("ZeroNetHome", "HoldReserve") and the display fallback ("Zero net home",
        // "Hold reserve") differ only in spaces and case.
        private static bool Is(QuarterlyInfoView q, string mode) =>
            (q.PlannedDisplayState ?? string.Empty).Replace(" ", string.Empty)
                .Equals(mode.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);

        private static string Ord(int rank)
        {
            int mod100 = rank % 100;
            string format = mod100 is >= 11 and <= 13
                ? "{0}th"
                : (rank % 10) switch { 1 => "{0}st", 2 => "{0}nd", 3 => "{0}rd", _ => "{0}th" };
            return Tf(format, rank);
        }

        // ── Per quarter ───────────────────────────────────────────────────────

        public QuarterWhy ExplainQuarter(QuarterlyInfoView q, IReadOnlyList<QuarterlyInfoView> all)
        {
            double chargeKWh = ChargeKWh(q);
            double dischargeKWh = DischargeKWh(q);

            var future = all.Where(x => x.Time > q.Time).ToList();
            double? maxFutureSell = future.Count > 0 ? future.Max(x => x.SellingPrice) : null;
            double? maxFutureBuy = future.Count > 0 ? future.Max(x => x.BuyingPrice) : null;
            double? minFutureBuy = future.Count > 0 ? future.Min(x => x.BuyingPrice) : null;

            int n = all.Count;
            int cheapBuyRank = all.Count(x => x.BuyingPrice < q.BuyingPrice) + 1;
            int dearSellRank = all.Count(x => x.SellingPrice > q.SellingPrice) + 1;

            // ── Charging from the grid ──
            if (chargeKWh > 0.01 && Is(q, "Charging"))
            {
                string tech = Tf("Charge {0:0.00} kWh @ {1} (import) — {2} cheapest of {3} quarters.", chargeKWh, Euro(q.BuyingPrice), Ord(cheapBuyRank), n);
                if (maxFutureBuy is double mb)
                    tech += Tf(" Later this energy avoids importing at up to {0}", Euro(mb));
                tech += maxFutureSell is double ms ? Tf(" or earns feed-in up to {0}.", Euro(ms)) : ".";
                return new QuarterWhy(
                    Tx("Charged from the grid: this is one of the cheapest hours, and the stored energy is worth more later than it costs now."),
                    tech);
            }

            // ── Discharging / exporting to the grid ──
            if (dischargeKWh > 0.01 && Is(q, "Discharging"))
            {
                if (q.SellingPrice < 0.0)
                    return new QuarterWhy(
                        Tx("Discharging to cover the house — not exported, because the feed-in price is negative."),
                        Tf("Discharge {0:0.00} kWh; feed-in {1} is negative, so this energy goes to your own use.", dischargeKWh, Euro(q.SellingPrice)));

                string t = Tf("Discharge {0:0.00} kWh @ {1} (feed-in) — {2} most expensive of {3}.", dischargeKWh, Euro(q.SellingPrice), Ord(dearSellRank), n);
                if (maxFutureSell is double ms2)
                    t += Tf(" Holding does not pay: the best later feed-in is {0}.", Euro(ms2));

                // Candidate F (shift discharge): later house cover gives way to this sale.
                int givenUp = future.Count(x => Is(x, "HoldReserve") && x.BuyingPrice < q.SellingPrice);
                if (givenUp > 0)
                    t += Tf(" {0} later quarter(s) import for the house instead, where buying is cheaper than this sale.", givenUp);
                return new QuarterWhy(
                    Tx("Sold to the grid: this is one of the most expensive hours, and selling now earns more than holding the energy."),
                    t);
            }

            // ── Battery off: solar surplus exported straight to the grid ──
            double solarKWh = q.SolarPowerPerQuarterHour;
            double houseKWh = Units.QuarterKWh(q.EstimatedConsumptionPerQuarterHour);
            if (Is(q, "Disabled") && solarKWh <= houseKWh)
                return new QuarterWhy(
                    Tx("Battery off: its energy went to a more expensive sale earlier, so the house imports here."),
                    Tf("No surplus to export; import @ {0} is cheaper than the sale this energy was used for.", Euro(q.BuyingPrice)));

            if (Is(q, "Disabled"))
            {
                string t = Tf("Solar export @ {0}.", Euro(q.SellingPrice));
                if (maxFutureBuy is double mb2)
                    t += Tf(" Storing would only pay off later (up to {0}), and after round-trip losses and wear that is less than selling now.", Euro(mb2));
                return new QuarterWhy(
                    Tx("Surplus solar goes straight to the grid: selling it now is better than storing it for later."),
                    t);
            }

            // ── HoldReserve: hold the energy, store live surplus, never discharge ──
            if (Is(q, "HoldReserve"))
            {
                double socKWh = q.PlannedChargeLeftWh / 1000.0;
                double reserveKWh = q.ChargeNeeded / 1000.0;
                string t = socKWh <= reserveKWh + 0.05
                    ? Tf("SOC {0:0.00} kWh is at the reserve ({1:0.00} kWh): covering the house would go below it.", socKWh, reserveKWh)
                    : Tf("SOC {0:0.00} kWh is kept for later", socKWh) +
                      (maxFutureSell is double ms3 ? Tf(" (feed-in up to {0}", Euro(ms3)) : " (") +
                      (maxFutureBuy is double mb4 ? Tf(", house cover up to {0})", Euro(mb4)) : ")") +
                      Tf("; importing now costs {0}.", Euro(q.BuyingPrice));
                if (chargeKWh > 0.01)
                    t += Tf(" Surplus solar is still stored ({0:0.00} kWh).", chargeKWh);
                return new QuarterWhy(
                    Tx("Hold reserve: the battery stores surplus solar if there is any, but does not cover the house — that energy is kept back, so the house imports."),
                    t);
            }

            // ── ZeroNetHome: self-consumption discharge ──
            if (dischargeKWh > 0.01)
                return new QuarterWhy(
                    Tx("The battery covers household use, so no (more expensive) power has to be imported from the grid."),
                    Tf("Self-consumption {0:0.00} kWh; avoids importing @ {1}. Actively buying or selling does not pay here.", dischargeKWh, Euro(q.BuyingPrice)));

            // ── ZeroNetHome: storing solar surplus ──
            if (chargeKWh > 0.01 || q.SolarPowerPerQuarterHour > 0.05)
            {
                string t = Tf("Storing solar ({0:0.00} kWh); feeding it in now would only earn {1}.", chargeKWh, Euro(q.SellingPrice));
                if (maxFutureBuy is double mb3)
                    t += Tf(" Later this energy is worth up to {0}.", Euro(mb3));
                return new QuarterWhy(
                    Tx("Surplus solar is stored for later; that is worth more than feeding it in now."),
                    t);
            }

            // ── Idle / balanced: nothing beats holding ──
            string idle = Tf("Import {0}, feed-in {1}; the spread does not cover the cycle cost (wear/round-trip).", Euro(q.BuyingPrice), Euro(q.SellingPrice));
            if (minFutureBuy is double mfb)
                idle += Tf(" Cheaper charging is available later ({0}).", Euro(mfb));
            return new QuarterWhy(
                Tx("No trade: no charge or sell action pays off after costs, so the battery stays balanced."),
                idle);
        }

        // ── Whole plan ────────────────────────────────────────────────────────

        public PlanWhy ExplainPlan(IReadOnlyList<QuarterlyInfoView> all)
        {
            // Explain the forward-looking part of the plan; fall back to everything if all is measured.
            var scope = all.Where(x => !x.IsMeasured).ToList();
            if (scope.Count == 0) scope = all.ToList();

            var charges = scope.Where(x => Is(x, "Charging") && ChargeKWh(x) > 0.01).ToList();
            var sells = scope.Where(x => Is(x, "Discharging") && DischargeKWh(x) > 0.01 && x.SellingPrice >= 0.0).ToList();
            var selfUse = scope.Where(x => Is(x, "ZeroNetHome") && DischargeKWh(x) > 0.01).ToList();
            var solarExport = scope.Where(x => Is(x, "Disabled")).ToList();
            var holding = scope.Where(x => Is(x, "HoldReserve")).ToList();

            double chargeKWh = charges.Sum(ChargeKWh);
            double sellKWh = sells.Sum(DischargeKWh);
            double selfKWh = selfUse.Sum(DischargeKWh);

            double avgBuy = chargeKWh > 0 ? charges.Sum(x => ChargeKWh(x) * x.BuyingPrice) / chargeKWh : 0.0;
            double avgSell = sellKWh > 0 ? sells.Sum(x => DischargeKWh(x) * x.SellingPrice) / sellKWh : 0.0;

            double chargeCost = charges.Sum(x => ChargeKWh(x) * x.BuyingPrice);
            double sellRevenue = sells.Sum(x => DischargeKWh(x) * x.SellingPrice);
            double selfSaving = selfUse.Sum(x => DischargeKWh(x) * x.BuyingPrice);

            double minBuy = scope.Count > 0 ? scope.Min(x => x.BuyingPrice) : 0.0;
            double maxSell = scope.Count > 0 ? scope.Max(x => x.SellingPrice) : 0.0;
            double endSocKWh = (scope.LastOrDefault()?.PlannedChargeLeftWh ?? 0.0) / 1000.0;

            string plain =
                Tf("The plan charges {0:0.0} kWh in the cheapest hours (avg {1}) and sells {2:0.0} kWh in the most expensive (avg {3}). It also covers {4:0.0} kWh of household use from the battery", chargeKWh, Euro(avgBuy), sellKWh, Euro(avgSell), selfKWh) +
                (solarExport.Count > 0 ? Tf(" and feeds surplus solar straight back in {0} quarters", solarExport.Count) : "") +
                (holding.Count > 0 ? Tf("; in {0} quarters it holds its energy (hold reserve) and the house imports", holding.Count) : "") +
                Tf(". At the end of the window {0:0.0} kWh is left in the battery as reserve. In short: buy when it is cheap, sell when it is expensive, and use the rest to avoid importing.", endSocKWh);

            var tech = new List<string>
            {
                Tf("Window: {0} quarters, import min {1}, feed-in max {2}.", scope.Count, Euro(minBuy), Euro(maxSell)),
                Tf("Grid charge: {0:0.0} kWh @ avg {1} (cost €{2:0.00}) across {3} quarters.", chargeKWh, Euro(avgBuy), chargeCost, charges.Count),
                Tf("Sold: {0:0.0} kWh @ avg {1} (revenue €{2:0.00}) across {3} quarters.", sellKWh, Euro(avgSell), sellRevenue, sells.Count),
                Tf("Self-consumption: {0:0.0} kWh (avoided import ≈ €{1:0.00}) across {2} quarters.", selfKWh, selfSaving, selfUse.Count),
                Tf("Solar export / battery off: {0} quarters.", solarExport.Count),
                Tf("Hold reserve (hold energy, store surplus, no house cover): {0} quarters.", holding.Count),
                Tf("End SOC (reserve): {0:0.0} kWh.", endSocKWh),
                Tf("Rough trade margin (sold − charged): €{0:0.00}, plus ≈ €{1:0.00} saved via self-consumption.", (sellRevenue - chargeCost), selfSaving)
            };

            return new PlanWhy(plain, tech);
        }
    }
}
