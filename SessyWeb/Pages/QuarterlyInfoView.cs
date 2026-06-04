namespace SessyWeb.Pages
{
    public class QuarterlyInfoView
    {
        public DateTime Time { get; set; }
        public int? SessionId { get; set; }
        public string DisplayState { get; set; } = string.Empty;
        public bool IsPriceExpected { get; set; }

        /// <summary>True when this quarter has real measured data (QuarterlyMeasurement).</summary>
        public bool IsMeasured { get; set; }
        public double Price { get; set; }
        public double BuyingPrice { get; set; }
        public double SellingPrice { get; set; }
        public double MarketPrice { get; set; }
        public double Profit { get; set; }
        public double SmoothedBuyingPrice { get; set; }
        public double VisualizeInChart { get; set; }
        public double SmoothedSellingPrice { get; set; }
        public double ChargeNeeded { get; set; }
        public double ChargeNeededPercentage { get; set; }
        public double ChargeLeft { get; set; }
        public double EstimatedConsumptionPerQuarterHour { get; set; }
        public double SolarPowerPerQuarterHour { get; set; }
        public double SmoothedSolarPower { get; set; }
        public double SolarGlobalRadiation { get; set; }
        public double ChargeLeftPercentage { get; set; }
        public double DeltaLowestPrice { get; set; }

        // Battery power in Watts — positive = charging, negative = discharging.
        // Planned values from MilpService, actual values from QuarterlyMeasurement.
        public double ChargePowerW { get; set; }
        public double DischargePowerW { get; set; }

        // Planned values from PlannedQuarter — shown alongside actuals for comparison.
        public double PlannedChargePowerW { get; set; }
        public double PlannedDischargePowerW { get; set; }
        public double PlannedChargeLeftWh { get; set; }
        public string PlannedDisplayState { get; set; } = string.Empty;

        // Explains why actual execution deviated from the plan (empty when matching).
        public string PlanDeviationReason { get; set; } = string.Empty;

        // Visual scaling matching ChargePowerVisual / DischargePowerVisual.
        public double PlannedChargePowerVisual => -(PlannedChargePowerW / 18000.0);
        public double PlannedDischargePowerVisual => PlannedDischargePowerW / 18000.0;
        public double PlannedChargeLeftVisual => PlannedChargeLeftWh / 100000.0;

        public double EstimatedConsumptionPerQuarterHourVisual => EstimatedConsumptionPerQuarterHour / 5000;
        public double ChargeNeededVisual => ChargeNeeded / 100000;
        public double ChargeLeftVisual => ChargeLeft / 100000;

        // Scale battery power to price axis: max 5400W = max 0.30 EUR on axis.
        // Discharging is positive (battery delivers energy = revenue = above axis).
        // Charging is negative (battery consumes energy = cost = below axis).
        public double ChargePowerVisual => -(ChargePowerW / 18000.0);
        public double DischargePowerVisual => DischargePowerW / 18000.0;

        // Small fixed band to indicate Zero net home quarters (neither charging nor discharging).
        public double ZeroNetHomeVisual
        {
            get
            {
                if (DisplayState == "Zero net home") return 0.03;
                return 0.0;
            }
        }
        public double AverageBuyingPrice { get; set; }
        public double AverageSellingPrice { get; set; }

        /// <summary>Inverter throttle percentage (0-100). 100 = full output, 0 = shutdown.</summary>
        public double ThrottlePct { get; set; } = 100.0;

        /// <summary>True when the selling price is negative (curtailment may be active).</summary>
        public bool IsCurtailed { get; set; }

        public double SolarPowerVisual => SolarPowerPerQuarterHour / 2.5;
        public double SmoothedSolarPowerVisual => SmoothedSolarPower / 2.5;

        public double SmoothedConsumptionPerQuarterHour { get; set; }
        public double SmoothedConsumptionVisual => SmoothedConsumptionPerQuarterHour / 5000;

        // Used by the "now" vertical line series — set to ChartMax by ChargingHoursChartComponent.
        public double NowLineHeight { get; set; } = 0.0;

        public override string ToString()
        {
            return $"Time: {Time}, IsPriceExpected: {IsPriceExpected}, VisualizeInChart: {VisualizeInChart}, Buying Price: {BuyingPrice}, Selling Price: {SellingPrice}, " +
                   $"Market Price: {MarketPrice}, Profit: {Profit}, Charge Left: {ChargeLeft}, " +
                   $"Estimated Consumption Per Quarter Hour: {EstimatedConsumptionPerQuarterHour}, " +
                   $"Solar Power Per Quarter Hour: {SolarPowerPerQuarterHour}, " +
                   $"Charge Left Percentage: {ChargeLeftPercentage}";
        }

    }
}