namespace SessyWeb.Pages
{
    public class QuarterlyInfoView
    {
        public DateTime Time { get; set; }
        public int? SessionId { get; set; }
        public string DisplayState { get; set; } = string.Empty;
        public bool IsPriceExpected { get; set; }
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

        public double EstimatedConsumptionPerQuarterHourVisual => EstimatedConsumptionPerQuarterHour / 2500;
        public double ChargeNeededVisual => ChargeNeeded / 100000;
        public double ChargeLeftVisual => ChargeLeft / 100000;
        public double SolarPowerVisual => SmoothedSolarPower / 2.5;

        // Scale battery power to price axis: max 5400W = max 0.30 EUR on axis.
        // Discharging is positive (battery delivers energy = revenue = above axis).
        // Charging is negative (battery consumes energy = cost = below axis).
        public double ChargePowerVisual => -(ChargePowerW / 18000.0);
        public double DischargePowerVisual => DischargePowerW / 18000.0;

        // Small fixed band to indicate ZeroNetHome quarters (neither charging nor discharging).
        public double ZeroNetHomeVisual
        {
            get
            {
                if (DisplayState == "ZeroNetHome") return 0.03;
                if (DisplayState == "Zero net home") return 0.03;
                return 0.0;
            }
        }
        public double AverageBuyingPrice { get; set; }
        public double AverageSellingPrice { get; set; }
        public double? SessionCost { get; set; } = null;

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