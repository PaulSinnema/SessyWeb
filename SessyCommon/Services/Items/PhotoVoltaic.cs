namespace SessyCommon.Services.Items
{
    public class PhotoVoltaic
    {
        public int PanelCount { get; set; }
        public double Tilt { get; set; }
        public double PeakPowerPerPanel { get; set; }
        public double Efficiency { get; set; }
        public double TotalArea { get; set; }       // Not used (yet)
        public double Orientation { get; set; }

        /// <summary>
        /// This is what your solar array can maximally produces on a day in Watts.
        /// It is equivalent to max 1000 gr (global radiation) as reported by Weer Online.
        /// </summary>
        public double PeakPowerForArray => PanelCount * PeakPowerPerPanel * Efficiency;
    }
}
