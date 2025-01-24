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
        public List<HourlyPrice> HourlyPrices { get; set; }

        /// <summary>
        /// The average price of all hourly prices in the session.
        /// </summary>
        public double AveragePrice => HourlyPrices.Average(hp => hp.Price);

        /// <summary>
        /// The first date in the session
        /// </summary>
        public DateTime First => HourlyPrices.Max(hp => hp.Time);

        /// <summary>
        /// The last date in the session
        /// </summary>
        public DateTime Last => HourlyPrices.Min(hp => hp.Time);

        public Session(Modes mode)
        {
            HourlyPrices = new List<HourlyPrice>();
            Mode = mode;
        }

        /// <summary>
        /// Add a price to the list
        /// </summary>
        public void AddHourlyPrice(HourlyPrice price)
        {
            HourlyPrices.Add(price);
        }

        /// <summary>
        /// Add neighbouring hours to the Session
        /// </summary>
        public void AddNeighbouringHours(List<HourlyPrice> hourlyPrices, int maxHours)
        {
            var currentPrice = hourlyPrices.First(hp => hp.Time == hourlyPrices.First().Time);

            var index = hourlyPrices.IndexOf(currentPrice);

            for (var i = 0; i < maxHours; i++)
            {
            }
        }
    }
}
