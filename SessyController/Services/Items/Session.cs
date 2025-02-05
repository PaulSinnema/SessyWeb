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

        private Modes _mode;

        public Modes Mode
        {
            get 
            {
                return _mode;
            }
            set
            {
                _mode = value;

                foreach (var hourlyPrice in HourlyInfos)
                {
                    hourlyPrice.SetModes(_mode);
                }
            }
        }

        /// <summary>
        /// All prices in the session
        /// </summary>
        private List<HourlyInfo> HourlyInfos { get; set; }

        /// <summary>
        /// Max hours of (dis)charging
        /// </summary>
        public int MaxHours { get; set; }

        /// <summary>
        /// The average price of all hourly prices in the session.
        /// </summary>
        public double AveragePrice => HourlyInfos.Average(hp => hp.Price);

        /// <summary>
        /// The first date in the session
        /// </summary>
        public DateTime FirstDate => HourlyInfos.Count > 0 ? HourlyInfos.Min(hp => hp.Time) : DateTime.MinValue;

        /// <summary>
        /// The last date in the session
        /// </summary>
        public DateTime LastDate => HourlyInfos.Count > 0 ? HourlyInfos.Max(hp => hp.Time) : DateTime.MaxValue;

        public Session(Modes mode, int maxHours)
        {
            HourlyInfos = new List<HourlyInfo>();
            MaxHours = maxHours;
            Mode = mode;
        }

        public IReadOnlyCollection<HourlyInfo> GetHourlyInfoList() => HourlyInfos.AsReadOnly();

        /// <summary>
        /// Add a price to the list
        /// </summary>
        public void AddHourlyInfo(HourlyInfo hourlyInfo)
        {
            hourlyInfo.SetModes(_mode);

            HourlyInfos.Add(hourlyInfo);
        }

        public void RemoveHourlyInfo(HourlyInfo hourlyInfo)
        {
            DisableChargingAndDischarging(hourlyInfo);

            HourlyInfos.Remove(hourlyInfo);
        }

        private static void DisableChargingAndDischarging(HourlyInfo hourlyInfo)
        {
            hourlyInfo.DisableCharging();
            hourlyInfo.DisableDischarging();
        }

        public void ClearHourlyInfoList()
        {
            foreach (var hourlyInfo in HourlyInfos)
            {
                DisableChargingAndDischarging(hourlyInfo);
            }
            HourlyInfos.Clear();
        }
    }
}
