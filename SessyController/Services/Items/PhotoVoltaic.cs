namespace SessyController.Services.Items
{
    public class PhotoVoltaic
    {
        public int PanelCount { get; set; }
        public int PeakPowerPerPanel { get; set; }
        public double Efficiency { get; set; }
        public double TotalArea { get; set; }
        public string? Orientation { get; set; } // I.e.: "South", "East", "West", "North", "Southwest"
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int TimeZoneOffset { get; set; } // Time zone (F.i. Netherlands = 1)
    }
}
