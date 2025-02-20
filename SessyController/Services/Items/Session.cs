using SessyController.Configurations;

namespace SessyController.Services.Items
{
    /// <summary>
    /// (Dis)charging session
    /// </summary>
    public class Session : IDisposable
    {
        public enum Modes
        {
            Unknown,
            Charging,
            Discharging
        };

        private Modes _mode { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private SettingsConfig _settingsConfig { get; set; }

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
        /// First hourlyInfo object in the session.
        /// </summary>
        public HourlyInfo? First => HourlyInfos.OrderBy(hi => hi.Time).FirstOrDefault();

        /// <summary>
        /// Last hourlyinfo object in the session.
        /// </summary>
        public HourlyInfo? Last => HourlyInfos.OrderByDescending(hi => hi.Time).FirstOrDefault();

        /// <summary>
        /// The first date in the session
        /// </summary>
        public DateTime FirstDate => First?.Time ?? DateTime.MinValue;

        /// <summary>
        /// The max this session needs to charge to.
        /// </summary>
        public double MaxChargeNeeded { get; set; }

        /// <summary>
        /// The last date in the session
        /// </summary>
        public DateTime LastDate => Last?.Time ?? DateTime.MaxValue;

        public Session(Modes mode,
                       int maxHours,
                       BatteryContainer batteryContainer,
                       SettingsConfig settingsConfig)
        {
            HourlyInfos = new List<HourlyInfo>();
            MaxHours = maxHours;
            Mode = mode;
            _batteryContainer = batteryContainer;
            _settingsConfig = settingsConfig;
            MaxChargeNeeded = _batteryContainer.GetTotalCapacity();
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

        private void DisableChargingAndDischarging(HourlyInfo hourlyInfo)
        {
            hourlyInfo.DisableCharging();
            hourlyInfo.DisableDischarging();
        }

        /// <summary>
        /// Returns true is the average price of this session is cheaper than the session submitted.
        /// </summary>
        public bool IsCheaper(Session session)
        {
            return HourlyInfos.Min(hi => hi.Price) < session.HourlyInfos.Min(hi => hi.Price);
        }

        /// <summary>
        /// Clear the hourlyInfos from the session.
        /// Caution! Clearing the list will not change the (dis)charging modes on
        /// the hourlyInfo items unless setModes = true!
        /// </summary>
        /// <param name="setModes">Set to true to change the modes in the listitems.</param>
        public void ClearHourlyInfoList(bool setModes = false)
        {
            if (setModes)
            {
                foreach (var hourlyInfo in HourlyInfos)
                {
                    DisableChargingAndDischarging(hourlyInfo);
                }
            }

            HourlyInfos.Clear();
        }

        public bool Contains(HourlyInfo hourlyInfo)
        {
            return HourlyInfos.Contains(hourlyInfo);
        }

        public bool RemoveAllAfter(int maxHours)
        {
            int index = maxHours;
            bool changed = false;

            switch (Mode)
            {
                case Modes.Charging:
                    {
                        var list = HourlyInfos.OrderBy(hi => hi.Price).ToList();

                        while (++index < list.Count)
                        {
                            RemoveHourlyInfo(list[index]);
                            changed = true;
                        }


                        break;
                    }

                case Modes.Discharging:
                    {
                        var list = HourlyInfos.OrderByDescending(hi => hi.Price).ToList();

                        while (++index < list.Count)
                        {
                            RemoveHourlyInfo(list[index]);
                            changed = true;
                        }


                        break;
                    }

                default:
                    throw new InvalidOperationException($"Invalid mode {Mode}");
            }

            return changed;
        }

        /// <summary>
        /// Get the hours needed to charge the batteries to 100%.
        /// </summary>
        internal int GetChargingHours()
        {
            // TODO: return (int)Math.Ceiling(MaxChargeNeeded / _batteryContainer.GetChargingCapacity());

            var chargeNeeded = MaxChargeNeeded - HourlyInfos.Average(hi => hi.ChargeLeft);

            if (chargeNeeded >= 0)
                return (int)Math.Ceiling(chargeNeeded / _batteryContainer.GetChargingCapacity());

            return 0;
        }

        public override string ToString()
        {
            return $"Session: {Mode}, FirstDate: {FirstDate}, LastDate {LastDate}, Count: {HourlyInfos.Count}, MaxChargeNeeded: {MaxChargeNeeded}, MaxHours: {MaxHours}";
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
            }
        }
    }
}
