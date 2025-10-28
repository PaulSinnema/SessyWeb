namespace SessyWeb.Pages
{
    public class QuarterlyInfoView
    {
        public DateTime Time { get; set; }
        public int? SessionId { get; set; }
        public string DisplayState { get; set; } = string.Empty;
        public double Price { get; set; }
        public double BuyingPrice { get; set; }
        public double SellingPrice { get; set; }
        public double MarketPrice { get; set; }
        public double Profit { get; set; }
        public double SmoothedBuyingPrice { get; set; }
        public double VisualizeInChart { get; set; }
        public double SmoothedSellingPrice { get; set; }
        public double ChargeNeeded { get; set; }
        public double ChargeLeft { get; set; }
        public double EstimatedConsumptionPerQuarterHour { get; set; }
        public double SolarPowerPerQuarterHour { get; set; }
        public double SmoothedSolarPower { get; set; }
        public double SolarGlobalRadiation { get; set; }
        public double ChargeLeftPercentage { get; set; }

        public double EstimatedConsumptionPerQuarterHourVisual => EstimatedConsumptionPerQuarterHour / 2500;
        public double ChargeNeededVisual => ChargeNeeded / 100000;
        public double ChargeLeftVisual => ChargeLeft / 100000;
        public double SolarPowerVisual => SmoothedSolarPower / 2.5;
        public double AverageBuyingPrice { get; set; }
        public double AverageSellingPrice { get; set; }

        public override string ToString()
        {
            return $"Time: {Time}, VisualizeInChart: {VisualizeInChart}, Buying Price: {BuyingPrice}, Selling Price: {SellingPrice}, " +
                   $"Market Price: {MarketPrice}, Profit: {Profit}, Charge Left: {ChargeLeft}, " +
                   $"Estimated Consumption Per Quarter Hour: {EstimatedConsumptionPerQuarterHour}, " +
                   $"Solar Power Per Quarter Hour: {SolarPowerPerQuarterHour}, " +
                   $"Charge Left Percentage: {ChargeLeftPercentage}";
        }

    }
}
