namespace SessyController.Services.Items
{
    public class PhotoVoltaic
    {
        public int PanelCount { get; set; }
        public double Tilt { get; set; }
        public double PeakPowerPerPanel { get; set; }
        public double Efficiency { get; set; }
        public double TotalArea { get; set; }
        public string Orientation { get; set; } = string.Empty; // I.e.: "South", "East", "West", "North", "Southwest"
    }
}
