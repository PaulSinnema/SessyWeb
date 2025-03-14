namespace SessyController.Services.Items
{
    public class FinancialResult
    {
        public DateTime Time { get; set; }
        public string YearMonth => Time.ToString("yyyyMMdd");
        public int Year => Time.Year;
        public int Month => Time.Month;
        public int Day => Time.Day;
        public int Hour => Time.Hour;
        public double Consumed { get; set; }
        public double Produced { get; set; }
        public double Grid => Produced - Consumed;
        public double Price { get; set; }
        public double Cost { get; set; }


    }
}
