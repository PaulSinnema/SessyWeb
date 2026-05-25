namespace SessyData.Model
{
    public class PlanVsActualStats
    {
        public int QuarterCount { get; set; }
        public double AvgSocDeviationPct { get; set; }
        public double MaxSocDeviationPct { get; set; }
        public double ModeAccuracyPct { get; set; }
        public int CurtailmentQuarters { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
    }
}