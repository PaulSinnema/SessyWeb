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
        public decimal Price { get; set; }
        public decimal Cost { get; set; }

        public override string ToString()
        {
            return $"{Time} Consumed: {Consumed}, Produced: {Produced}, Grid: {Grid}, Price {Price}, Cost: {Cost}";
        }
    }

    public class FinancialMonthResult
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public string YearMonth => $"{Year}/{Month}";
        public List<FinancialResult>? FinancialResultsList { get; set; } = new();
        public decimal TotalCost => FinancialResultsList!.Sum(fr => fr.Cost);

        public override string ToString()
        {
            return $"{Year}-{Month}";
        }
    }
}
