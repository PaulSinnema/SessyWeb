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
        public DateTime First => PriceList.Max(hp => hp.Time);

        /// <summary>
        /// The last date in the session
        /// </summary>
        public DateTime Last => PriceList.Min(hp => hp.Time);

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

        /// <summary>
        /// Add neighbouring hours to the Session
        /// </summary>
        public void CompleteSession(List<HourlyPrice> hourlyPrices, int maxHours, double cycleCost, double averagePrice)
        {
            if (PriceList.Count != 1)
                throw new InvalidOperationException($"Session has zero or more than 1 hourly price.");

            var index = hourlyPrices.IndexOf(PriceList[0]);
            var prev = index - 1;
            var next = index + 1;

            for (var i = 0; i < maxHours - 1; i++)
            {
                switch (Mode)
                {
                    case Modes.Charging:
                        {
                            if (prev >= 0)
                            {
                                if (next < hourlyPrices.Count)
                                {
                                    if (hourlyPrices[next].Price < hourlyPrices[prev].Price)
                                    {
                                        if(hourlyPrices[next].Price < averagePrice)
                                            AddHourlyPrice(hourlyPrices[next++]);
                                    }
                                    else
                                    {
                                        if (hourlyPrices[prev].Price < averagePrice)
                                            AddHourlyPrice(hourlyPrices[prev--]);
                                    }
                                }
                                else
                                {
                                        if (hourlyPrices[prev].Price < averagePrice)
                                            AddHourlyPrice(hourlyPrices[prev--]);
                                }
                            }
                            else
                            {
                                if (next < hourlyPrices.Count)
                                {
                                    if (hourlyPrices[next].Price < averagePrice)
                                        AddHourlyPrice(hourlyPrices[next++]);
                                }
                            }

                            break;
                        }

                    case Modes.Discharging:
                        {
                            if (prev >= 0)
                            {
                                if (next < hourlyPrices.Count)
                                {
                                    if (hourlyPrices[next].Price > hourlyPrices[prev].Price)
                                    {
                                        if (hourlyPrices[next].Price > averagePrice)
                                            AddHourlyPrice(hourlyPrices[next++]);
                                    }
                                    else
                                    {
                                        if (hourlyPrices[prev].Price > averagePrice)
                                            AddHourlyPrice(hourlyPrices[prev--]);
                                    }
                                }
                                else
                                {
                                    if (hourlyPrices[prev].Price > averagePrice)
                                        AddHourlyPrice(hourlyPrices[prev--]); 
                                }
                            }
                            else
                            {
                                if (next < hourlyPrices.Count)
                                {
                                     if (hourlyPrices[next].Price > averagePrice)
                                        AddHourlyPrice(hourlyPrices[next++]);
                                }
                            }

                            break;
                        }

                    default:
                        break;
                }
            }
        }
    }
}
