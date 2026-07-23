using SessyController.Services.Items;
using SessyData.Model;
using static SessyController.Services.Items.ChargingModes;

namespace SessyWeb.Pages
{
    /// <summary>
    /// Immutable view model for chart/UI rendering.
    /// Constructed once from QuarterlyInfo + optional PlannedQuarter and hardware overrides.
    /// All properties are read-only after construction — no external mutation.
    /// </summary>
    public class QuarterlyInfoView
    {
        public QuarterlyInfoView(
            QuarterlyInfo qi,
            double totalCapacityWh,
            PlannedQuarter? plannedQuarter = null,
            double averageBuyingPrice = 0.0,
            double averageSellingPrice = 0.0,
            string? actualDisplayState = null,
            double? actualPowerW = null,
            double currentThrottlePct = 100.0)
        {
            // ── Identity ─────────────────────────────────────────────────────
            Time = qi.Time;
            IsMeasured = qi.IsMeasured;
            IsPriceExpected = qi.IsPriceExpected;

            // ── Prices ───────────────────────────────────────────────────────
            BuyingPrice = qi.BuyingPrice;
            SellingPrice = qi.SellingPrice;
            MarketPrice = qi.MarketPrice;
            Price = qi.Price;
            SmoothedBuyingPrice = qi.SmoothedBuyingPrice;
            SmoothedSellingPrice = qi.SmoothedSellingPrice;
            AverageBuyingPrice = averageBuyingPrice;
            AverageSellingPrice = averageSellingPrice;
            DeltaLowestPrice = qi.DeltaLowestPrice;
            ProjectedCostBasisEur = qi.ProjectedCostBasisEur;
            IsCurtailed = qi.SellingPriceIsNegative;
            ThrottlePct = qi.SellingPriceIsNegative ? currentThrottlePct : 100.0;

            // ── Profit ───────────────────────────────────────────────────────
            Profit = qi.Profit;

            // ── SOC ──────────────────────────────────────────────────────────
            ChargeLeft = qi.ChargeLeftWh;
            ChargeNeeded = qi.ChargeNeededWh;
            ChargeLeftPercentage = totalCapacityWh > 0 ? qi.ChargeLeftWh / totalCapacityWh * 100.0 : 0.0;
            ChargeNeededPercentage = totalCapacityWh > 0 ? qi.ChargeNeededWh / totalCapacityWh * 100.0 : 0.0;

            // ── Solar / consumption ───────────────────────────────────────────
            EstimatedConsumptionPerQuarterHour = qi.EstimatedConsumptionPerQuarterInWatts;
            SolarPowerPerQuarterHour = qi.SolarPowerPerQuarterHour;
            SmoothedSolarPower = qi.SmoothedSolarPower;
            SolarGlobalRadiation = qi.SolarGlobalRadiation;
            SmoothedConsumptionPerQuarterHour = qi.SmoothedConsumptionPerQuarterHour;

            // ── Actual battery power ─────────────────────────────────────────
            // Use hardware reading for current executing quarter; else plan values.
            if (actualPowerW.HasValue)
            {
                ChargePowerW = actualPowerW.Value < 0 ? Math.Abs(actualPowerW.Value) : 0.0;
                DischargePowerW = actualPowerW.Value > 0 ? actualPowerW.Value : 0.0;
                HasActualPower = true;
            }
            else
            {
                // For a measured quarter qi was built from the stored measurement, so these
                // fields hold the measured power despite their "Planned" name. For a future
                // quarter they hold the plan, which is not an actual reading.
                ChargePowerW = qi.PlannedChargePowerW;
                DischargePowerW = qi.PlannedDischargePowerW;
                HasActualPower = qi.IsMeasured;
            }

            DisplayState = actualDisplayState ?? qi.GetDisplayMode() ?? string.Empty;

            // ── Planned values (from PlannedQuarter DB record) ───────────────
            PlannedDisplayState = plannedQuarter?.PlannedMode ?? qi.GetDisplayMode() ?? string.Empty;
            PlannedChargeLeftWh = plannedQuarter?.PlannedChargeLeftWh ?? qi.ChargeLeftWh;

            if (plannedQuarter != null)
            {
                if (string.Equals(plannedQuarter.PlannedMode, "Charging", StringComparison.OrdinalIgnoreCase))
                    PlannedChargePowerW = Math.Abs(plannedQuarter.PlannedPowerW);
                else if (string.Equals(plannedQuarter.PlannedMode, "Discharging", StringComparison.OrdinalIgnoreCase))
                    PlannedDischargePowerW = Math.Abs(plannedQuarter.PlannedPowerW);
            }
            else
            {
                PlannedChargePowerW = qi.PlannedChargePowerW;
                PlannedDischargePowerW = qi.PlannedDischargePowerW;
            }

            // Unthrottled planned power — absolute value, direction derived from planned mode.
            PlannedUnthrottledPowerW = qi.PlannedUnthrottledPowerW;

            PlanDeviationReason = DeterminePlanDeviationReason(
                DisplayState, ChargePowerW, DischargePowerW,
                PlannedDisplayState, IsCurtailed, SellingPrice);
        }

        /// <summary>Parameterless constructor for backwards compatibility.</summary>
        public QuarterlyInfoView()
        {
            DisplayState = string.Empty;
            PlannedDisplayState = string.Empty;
            PlanDeviationReason = string.Empty;
        }

        // ── Identity ─────────────────────────────────────────────────────────
        public DateTime Time { get; }
        public bool IsMeasured { get; }
        public bool IsPriceExpected { get; }
        public int? SessionId => null;

        // ── Prices ───────────────────────────────────────────────────────────
        public double BuyingPrice { get; }
        public double SellingPrice { get; }
        public double MarketPrice { get; }
        public double Price { get; }
        public double SmoothedBuyingPrice { get; }
        public double SmoothedSellingPrice { get; }
        public double AverageBuyingPrice { get; }
        public double AverageSellingPrice { get; }
        public double DeltaLowestPrice { get; }
        public double ProjectedCostBasisEur { get; }
        public bool IsCurtailed { get; }
        public double ThrottlePct { get; }

        // ── Profit ───────────────────────────────────────────────────────────
        public double Profit { get; }

        // ── SOC ──────────────────────────────────────────────────────────────
        public double ChargeLeft { get; }
        public double ChargeNeeded { get; }
        public double ChargeLeftPercentage { get; }
        public double ChargeNeededPercentage { get; }

        // ── Solar / consumption ───────────────────────────────────────────────
        public double EstimatedConsumptionPerQuarterHour { get; }
        public double SolarPowerPerQuarterHour { get; }
        public double SmoothedSolarPower { get; }
        public double SolarGlobalRadiation { get; }
        public double SmoothedConsumptionPerQuarterHour { get; }

        // ── Display state & battery power ────────────────────────────────────
        public string DisplayState { get; }
        public double ChargePowerW { get; }
        public double DischargePowerW { get; }

        /// <summary>
        /// True when ChargePowerW/DischargePowerW hold a real reading — a hardware value for the
        /// executing quarter, or a stored measurement for a past one — rather than a copy of the
        /// plan. False for future quarters.
        /// </summary>
        public bool HasActualPower { get; }

        // ── Planned values ────────────────────────────────────────────────────
        public string PlannedDisplayState { get; }
        public double PlannedChargeLeftWh { get; }
        public double PlannedChargePowerW { get; }
        public double PlannedDischargePowerW { get; }
        public double PlannedUnthrottledPowerW { get; }
        public string PlanDeviationReason { get; }

        // ── Now line (mutable — set by chart component) ───────────────────────
        public double NowLineHeight { get; set; } = 0.0;

        // ── Visuals ───────────────────────────────────────────────────────────
        public double VisualizeInChart =>
            DisplayState == "Charging" ? -0.1 :
            DisplayState == "Discharging" ? 0.1 :
            DisplayState == "Zero net home" ? 0.03 : 0.0;

        // Actual battery power. Only meaningful for quarters that have a real reading; the
        // series binds to a filtered list (see ChargingHoursChartComponent.ActualPowerPoints)
        // rather than returning null here — Radzen reads ValueProperty through a non-nullable
        // getter in CartesianSeries.DataAt/TooltipY, so a null throws while the crosshair moves.
        public double ChargePowerVisual => -(ChargePowerW / 18000.0);
        public double DischargePowerVisual => DischargePowerW / 18000.0;

        public double PlannedChargePowerVisual => -(PlannedChargePowerW / 18000.0);
        public double PlannedDischargePowerVisual => PlannedDischargePowerW / 18000.0;

        // Unthrottled planned ceiling — only non-zero when throttle actually reduces power.
        // Used as the outer boundary of the throttle-loss band.
        public double UnthrottledChargePowerVisual =>
            PlannedChargePowerW > 0 && PlannedUnthrottledPowerW > PlannedChargePowerW
                ? -(PlannedUnthrottledPowerW / 18000.0)
                : PlannedChargePowerVisual;

        public double UnthrottledDischargePowerVisual =>
            PlannedDischargePowerW > 0 && PlannedUnthrottledPowerW > PlannedDischargePowerW
                ? PlannedUnthrottledPowerW / 18000.0
                : PlannedDischargePowerVisual;
        public double PlannedChargeLeftVisual => PlannedChargeLeftWh / 100000.0;

        public double ChargeNeededVisual => ChargeNeeded / 100000.0;
        public double ChargeLeftVisual => ChargeLeft / 100000.0;

        public double EstimatedConsumptionPerQuarterHourVisual => EstimatedConsumptionPerQuarterHour / 5000.0;
        public double SmoothedConsumptionVisual => SmoothedConsumptionPerQuarterHour / 5000.0;

        public double SolarPowerVisual => SolarPowerPerQuarterHour / 2.5;
        public double SmoothedSolarPowerVisual => SmoothedSolarPower / 2.5;

        public double ZeroNetHomeVisual =>
            DisplayState == "Zero net home" ? 0.03 : 0.0;

        // ── Plan deviation ────────────────────────────────────────────────────
        private static string DeterminePlanDeviationReason(
            string displayState, double chargePowerW, double dischargePowerW,
            string plannedDisplayState, bool isCurtailed, double sellingPrice)
        {
            bool actualCharging = chargePowerW > 10;
            bool actualDischarging = dischargePowerW > 10;
            string actualMode = actualCharging ? "Charging" : actualDischarging ? "Discharging" : "ZeroNetHome";
            string plannedMode = string.IsNullOrEmpty(plannedDisplayState) ? "ZeroNetHome" : plannedDisplayState;

            if (string.Equals(actualMode, plannedMode, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            if (string.Equals(plannedMode, "Discharging", StringComparison.OrdinalIgnoreCase) && !actualDischarging)
            {
                if (isCurtailed || sellingPrice < 0.0)
                    return "Negative export price — discharge skipped.";
                return "Better selling price expected in other quarters — energy held back.";
            }

            if (string.Equals(plannedMode, "Charging", StringComparison.OrdinalIgnoreCase) && !actualCharging)
                return "Solar surplus covers consumption — grid charge skipped.";

            return $"Plan {plannedMode.ToLowerInvariant()}, actual {actualMode.ToLowerInvariant()}.";
        }

        public override string ToString() =>
            $"Time: {Time}, IsPriceExpected: {IsPriceExpected}, BuyingPrice: {BuyingPrice}, " +
            $"SellingPrice: {SellingPrice}, Profit: {Profit}, ChargeLeft: {ChargeLeft}, " +
            $"ChargeLeftPct: {ChargeLeftPercentage:F1}%";
    }
}