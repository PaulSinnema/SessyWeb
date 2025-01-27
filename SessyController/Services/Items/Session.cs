namespace SessyController.Services.Items
{
    /// <summary>
    /// (Dis)charging session
    /// </summary>
    public class Session
    {
        public enum Modes
        {
            Charging,
            Discharging
        };

        public Modes Mode { get; set; }

        /// <summary>
        /// All prices in the session
        /// </summary>
        public List<HourlyPrice> PriceList { get; set; }

        /// <summary>
        /// Max hours of (dis)charging
        /// </summary>
        public int MaxHours { get; set; }

        /// <summary>
        /// The average price of all hourly prices in the session.
        /// </summary>
        public double AveragePrice => PriceList.Average(hp => hp.Price);

        /// <summary>
        /// The first date in the session
        /// </summary>
        public DateTime First => PriceList.Count > 0 ? PriceList.Min(hp => hp.Time) : DateTime.MinValue;

        /// <summary>
        /// The last date in the session
        /// </summary>
        public DateTime Last => PriceList.Count > 0 ? PriceList.Max(hp => hp.Time) : DateTime.MaxValue;

        public Session(Modes mode, int maxHours)
        {
            PriceList = new List<HourlyPrice>();
            MaxHours = maxHours;
            Mode = mode;
        }

        /// <summary>
        /// Add a price to the list
        /// </summary>
        public void AddHourlyPrice(HourlyPrice price)
        {
            PriceList.Add(price);
        }
    }
}
