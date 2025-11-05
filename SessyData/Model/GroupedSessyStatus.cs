namespace SessyData.Model
{
    public class GroupedSessyStatus
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public string? StatusDetails { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public string DurationVisual => Duration.ToString(@"hh\:mm\:ss");

        public override string ToString()
        {
            return $"StartTime: {StartTime}, EndTime: {EndTime}, Duration: {Duration}, Name: {Name}, Status: {Status}, Details: {StatusDetails}";
        }
    }
}
