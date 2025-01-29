namespace SessyController.Services.Items
{
    public class PhotoVoltaic
    {
        public int PanelCount { get; set; }
        public double PeakPowerPerPanel { get; set; }
        public double Efficiency { get; set; }
        public double TotalArea { get; set; }
        public string? Orientation { get; set; } // I.e.: "South", "East", "West", "North", "Southwest"
    }
}
