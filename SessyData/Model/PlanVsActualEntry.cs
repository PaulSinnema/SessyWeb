namespace SessyData.Model
{
    public class PlanVsActualEntry
    {
        public DateTime Time { get; set; }

        // Planned
        public string PlannedMode { get; set; } = string.Empty;
        public double PlannedPowerW { get; set; }
        public double PlannedChargeLeftWh { get; set; }
        public double SellingPriceEurKWh { get; set; }
        public double BuyingPriceEurKWh { get; set; }
        public double SolarForecastW { get; set; }
        public double ConsumptionForecastW { get; set; }

        // Actual
        public string ActualMode { get; set; } = string.Empty;
        public double ActualPowerW { get; set; }
        public double ActualSocWh { get; set; }
        public string CurtailmentMode { get; set; } = string.Empty;
        public string StateMachineReason { get; set; } = string.Empty;

        // Derived
        public double SocDeviationPct { get; set; }
        public bool ModeMatch { get; set; }
    }
}