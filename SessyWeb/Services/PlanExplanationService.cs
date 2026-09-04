using SessyWeb.Pages;

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
    /// mode/ActionMode, and the price / round-trip / cycle-cost trade-offs. If the planner changes
    /// (a new mode, a different threshold, a changed price build-up), update this explainer too, or
    /// "Why this plan?" will describe behaviour the planner no longer follows.
    /// </remarks>
    public sealed class PlanExplanationService
    {
        private const double QuarterHours = 0.25;

        private static string Euro(double v) => $"€{v:0.000}/kWh";
        private static double ChargeKWh(QuarterlyInfoView q) => q.PlannedChargePowerW * QuarterHours / 1000.0;
        private static double DischargeKWh(QuarterlyInfoView q) => q.PlannedDischargePowerW * QuarterHours / 1000.0;
        private static bool Is(QuarterlyInfoView q, string mode) =>
            (q.PlannedDisplayState ?? string.Empty).Equals(mode, StringComparison.OrdinalIgnoreCase);

        private static string Ord(int rank)
        {
            int mod100 = rank % 100;
            string suffix = mod100 is >= 11 and <= 13
                ? "th"
                : (rank % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return $"{rank}{suffix}";
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
                string tech = $"Charge {chargeKWh:0.00} kWh @ {Euro(q.BuyingPrice)} (import) — {Ord(cheapBuyRank)} cheapest of {n} quarters.";
                if (maxFutureBuy is double mb)
                    tech += $" Later this energy avoids importing at up to {Euro(mb)}";
                tech += maxFutureSell is double ms ? $" or earns feed-in up to {Euro(ms)}." : ".";
                return new QuarterWhy(
                    "Charged from the grid: this is one of the cheapest hours, and the stored energy is worth more later than it costs now.",
                    tech);
            }

            // ── Discharging / exporting to the grid ──
            if (dischargeKWh > 0.01 && Is(q, "Discharging"))
            {
                if (q.SellingPrice < 0.0)
                    return new QuarterWhy(
                        "Discharging to cover the house — not exported, because the feed-in price is negative.",
                        $"Discharge {dischargeKWh:0.00} kWh; feed-in {Euro(q.SellingPrice)} is negative, so this energy goes to your own use.");

                string t = $"Discharge {dischargeKWh:0.00} kWh @ {Euro(q.SellingPrice)} (feed-in) — {Ord(dearSellRank)} most expensive of {n}.";
                if (maxFutureSell is double ms2)
                    t += $" Holding does not pay: the best later feed-in is {Euro(ms2)}.";
                return new QuarterWhy(
                    "Sold to the grid: this is one of the most expensive hours, and selling now earns more than holding the energy.",
                    t);
            }

            // ── Battery off: solar surplus exported straight to the grid ──
            if (Is(q, "Disabled"))
            {
                string t = $"Solar export @ {Euro(q.SellingPrice)}.";
                if (maxFutureBuy is double mb2)
                    t += $" Storing would only pay off later (up to {Euro(mb2)}), and after round-trip losses and wear that is less than selling now.";
                return new QuarterWhy(
                    "Surplus solar goes straight to the grid: selling it now is better than storing it for later.",
                    t);
            }

            // ── ZeroNetHome: self-consumption discharge ──
            if (dischargeKWh > 0.01)
                return new QuarterWhy(
                    "The battery covers household use, so no (more expensive) power has to be imported from the grid.",
                    $"Self-consumption {dischargeKWh:0.00} kWh; avoids importing @ {Euro(q.BuyingPrice)}. Actively buying or selling does not pay here.");

            // ── ZeroNetHome: storing solar surplus ──
            if (chargeKWh > 0.01 || q.SolarPowerPerQuarterHour > 0.05)
            {
                string t = $"Storing solar ({chargeKWh:0.00} kWh); feeding it in now would only earn {Euro(q.SellingPrice)}.";
                if (maxFutureBuy is double mb3)
                    t += $" Later this energy is worth up to {Euro(mb3)}.";
                return new QuarterWhy(
                    "Surplus solar is stored for later; that is worth more than feeding it in now.",
                    t);
            }

            // ── Idle / balanced: nothing beats holding ──
            string idle = $"Import {Euro(q.BuyingPrice)}, feed-in {Euro(q.SellingPrice)}; the spread does not cover the cycle cost (wear/round-trip).";
            if (minFutureBuy is double mfb)
                idle += $" Cheaper charging is available later ({Euro(mfb)}).";
            return new QuarterWhy(
                "No trade: no charge or sell action pays off after costs, so the battery stays balanced.",
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
                $"The plan charges {chargeKWh:0.0} kWh in the cheapest hours (avg {Euro(avgBuy)}) and " +
                $"sells {sellKWh:0.0} kWh in the most expensive (avg {Euro(avgSell)}). " +
                $"It also covers {selfKWh:0.0} kWh of household use from the battery" +
                (solarExport.Count > 0 ? $" and feeds surplus solar straight back in {solarExport.Count} quarters" : "") +
                $". At the end of the window {endSocKWh:0.0} kWh is left in the battery as reserve. " +
                "In short: buy when it is cheap, sell when it is expensive, and use the rest to avoid importing.";

            var tech = new List<string>
            {
                $"Window: {scope.Count} quarters, import min {Euro(minBuy)}, feed-in max {Euro(maxSell)}.",
                $"Grid charge: {chargeKWh:0.0} kWh @ avg {Euro(avgBuy)} (cost €{chargeCost:0.00}) across {charges.Count} quarters.",
                $"Sold: {sellKWh:0.0} kWh @ avg {Euro(avgSell)} (revenue €{sellRevenue:0.00}) across {sells.Count} quarters.",
                $"Self-consumption: {selfKWh:0.0} kWh (avoided import ≈ €{selfSaving:0.00}) across {selfUse.Count} quarters.",
                $"Solar export (battery off): {solarExport.Count} quarters.",
                $"End SOC (reserve): {endSocKWh:0.0} kWh.",
                $"Rough trade margin (sold − charged): €{(sellRevenue - chargeCost):0.00}, plus ≈ €{selfSaving:0.00} saved via self-consumption."
            };

            return new PlanWhy(plain, tech);
        }
    }
}
