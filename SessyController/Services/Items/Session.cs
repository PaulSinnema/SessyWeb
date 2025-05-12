using SessyCommon.Extensions;
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
            Discharging,
            ZeroNetHome,
            Disabled
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
            private set
            {
                _mode = value;

                foreach (var hourlyPrice in SessionHourlyInfos)
                {
                    hourlyPrice.SetModes(_mode);
                }
            }
        }

        /// <summary>
        /// The charging power in Watts for (dis)charging.
        /// </summary>
        public double ChargingPowerInWatts
        {
            get
            {
                var currentHourlyInfo = _sessions.GetCurrentHourlyInfo();
                var chargeNeeded = currentHourlyInfo.ChargeNeeded;
                var totalCapacity = _batteryContainer.GetTotalCapacity();
                var prices = SessionHourlyInfos.Sum(hi => hi.BuyingPrice);

                switch (Mode)
                {
                    case Modes.Charging:
                        {
                            var toCharge = chargeNeeded; 
                            var capacity = _batteryContainer.GetChargingCapacityPerQuarter();

                            foreach (var hourlyInfo in SessionHourlyInfos.OrderBy(hi => hi.BuyingPrice))
                            {
                                var watts = Math.Min(capacity, toCharge);
                                toCharge -= watts;

                                if (hourlyInfo.Time == currentHourlyInfo.Time)
                                {
                                    return Math.Max(watts, 0.0);
                                }
                            }

                            throw new InvalidOperationException($"Could not find current hourly info {currentHourlyInfo}");
                        }

                    case Modes.Discharging:
                        {
                            var toDischarge = totalCapacity - chargeNeeded;
                            var capacity = _batteryContainer.GetDischargingCapacityPerQuarter();

                            foreach (var hourlyInfo in SessionHourlyInfos.OrderByDescending(hi => hi.SellingPrice))
                            {
                                var watts = Math.Min(capacity, toDischarge);
                                toDischarge -= watts;

                                if (hourlyInfo.Time == currentHourlyInfo.Time)
                                    return Math.Max(watts, 0.0);
                            }

                            throw new InvalidOperationException($"Could not find current hourly info {currentHourlyInfo}");
                        }

                    default:
                        throw new InvalidOperationException($"Invalid mode {this}");
                }
            }
        }

        /// <summary>
        /// Returns true if the hourlyInfo is contained in the sessions list.
        /// </summary>
        public bool Contains(HourlyInfo hourlyInfo)
        {
            return SessionHourlyInfos.Any(hi => hi.Time.DateFloorQuarter() == hourlyInfo.Time.DateFloorQuarter());
        }

        /// <summary>
        /// All prices in the session
        /// </summary>
        private List<HourlyInfo> SessionHourlyInfos { get; set; }

        private Sessions _sessions { get; set; }

        private TimeZoneService _timeZoneService { get; set; }

        /// <summary>
        /// Max hours of (dis)charging
        /// </summary>
        public int MaxQuarters { get; set; }

        /// <summary>
        /// The average price of all hourly prices in the session.
        /// </summary>
        public double AveragePrice => SessionHourlyInfos.Count > 0 ? SessionHourlyInfos.Average(hp => hp.BuyingPrice) : 0.0;

        /// <summary>
        /// First hourlyInfo object in the session.
        /// </summary>
        public HourlyInfo? First => SessionHourlyInfos.OrderBy(hi => hi.Time).FirstOrDefault();

        /// <summary>
        /// Last hourlyinfo object in the session.
        /// </summary>
        public HourlyInfo? Last => SessionHourlyInfos.OrderByDescending(hi => hi.Time).FirstOrDefault();

        /// <summary>
        /// The first date in the session
        /// </summary>
        public DateTime FirstDateTime => First?.Time ?? DateTime.MinValue;

        /// <summary>
        /// Returns true if any of the prices is negative.
        /// </summary>
        public bool PricesAnyNegative => SessionHourlyInfos.Any(hi => hi.BuyingPrice < 0.0);

        public bool PricesAllPositive => SessionHourlyInfos.All(hi => hi.BuyingPrice >= 0.0);

        /// <summary>
        /// The last date in the session
        /// </summary>
        public DateTime LastDateTime => Last?.Time ?? DateTime.MaxValue;

        public Session(Sessions sessions,
                       TimeZoneService timeZoneService,
                       Modes mode,
                       int maxQuarters,
                       BatteryContainer batteryContainer,
                       SettingsConfig settingsConfig)
        {
            SessionHourlyInfos = new List<HourlyInfo>();
            _sessions = sessions;
            _timeZoneService = timeZoneService;
            MaxQuarters = maxQuarters;
            Mode = mode;
            _batteryContainer = batteryContainer;
            _settingsConfig = settingsConfig;
        }

        /// <summary>
        /// Returns the total charge needed for this session.
        /// </summary>
        /// <returns></returns>
        public double GetChargeNeeded()
        {
            if (Mode != Modes.Charging)
                throw new InvalidOperationException($"Invalid mode {this}");

            var hours = GetHours();
            var charge = _batteryContainer.GetChargingCapacityPerQuarter();

            return Math.Min(_batteryContainer.GetTotalCapacity(), hours * charge);
        }

        /// <summary>
        /// Sets the charge needed for each hourlyInfo object in this session.
        /// </summary>
        public void SetChargeNeeded(double charge)
        {
            SessionHourlyInfos.ForEach(hi => hi.ChargeNeeded = charge);
        }

        /// <summary>
        /// Gets the hourlyInofos list sorted by Time as a readonly collection.
        /// </summary>
        public IReadOnlyCollection<HourlyInfo> GetHourlyInfoList() => SessionHourlyInfos.OrderBy(hi => hi.Time).ToList().AsReadOnly();

        /// <summary>
        /// Add a hourly info object to the list if not already in the list.
        /// </summary>
        public void AddHourlyInfo(HourlyInfo hourlyInfo)
        {
            if (!Contains(hourlyInfo))
            {
                hourlyInfo.SetModes(_mode);

                SessionHourlyInfos.Add(hourlyInfo);
            }
        }

        public void RemoveHourlyInfo(HourlyInfo hourlyInfo)
        {
            if (Contains(hourlyInfo))
            {
                DisableChargingAndDischarging(hourlyInfo);

                SessionHourlyInfos.Remove(hourlyInfo);
            }
        }

        private void DisableChargingAndDischarging(HourlyInfo hourlyInfo)
        {
            hourlyInfo.DisableCharging();
            hourlyInfo.DisableDischarging();
        }

        /// <summary>
        /// Returns if this session on average is more profitable.
        /// </summary>
        public bool IsMoreProfitable(Session session)
        {
            if (session.Mode != Mode)
                throw new InvalidOperationException($"Modes should be the same of both sessions {Mode} != {session.Mode}");

            switch (Mode)
            {
                case Modes.Charging:
                    return SessionHourlyInfos.Average(hi => hi.BuyingPrice) < session.SessionHourlyInfos.Average(hi => hi.BuyingPrice);

                case Modes.Discharging:
                    return SessionHourlyInfos.Average(hi => hi.SellingPrice) > session.SessionHourlyInfos.Average(hi => hi.SellingPrice);

                default:
                    throw new InvalidOperationException($"Wrong mode: {Mode}");
            }
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
                foreach (var hourlyInfo in SessionHourlyInfos)
                {
                    DisableChargingAndDischarging(hourlyInfo);
                }
            }

            SessionHourlyInfos.Clear();
        }

        public bool RemoveAllAfter(int maxQuarters)
        {
            bool changed = false;

            // Once the session has started don't remove anything before now.
            var now = _timeZoneService.Now.DateFloorQuarter();
            var hourlyInfos = SessionHourlyInfos.Where(hi => hi.Time > now).ToList();

            switch (Mode)
            {
                case Modes.Charging:
                    {
                        var list = hourlyInfos
                            .OrderByDescending(hi => hi.BuyingPrice)
                            .ThenBy(hi => hi.Time)
                            .ToList();

                        changed = RemoveTheHours(maxQuarters, list);

                        break;
                    }

                case Modes.Discharging:
                    {
                        var list = hourlyInfos
                            .OrderBy(hi => hi.SellingPrice)
                            .ThenBy(hi => hi.Time)
                            .ToList();

                        changed = RemoveTheHours(maxQuarters, list);

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Invalid mode {Mode}");
            }

            return changed;
        }

        private bool RemoveTheHours(int maxHours, List<HourlyInfo> list)
        {
            bool changed = false;
            int count = 0;

            if (maxHours <= list.Count)
            {
                count = list.Count - maxHours;

                for (int index = 0; index < count; index++)
                {
                    try
                    {
                        RemoveHourlyInfo(list[index]);
                    }
                    catch (Exception)
                    {
                        throw new InvalidOperationException($"Index {index}, count {count}, list.Count {list.Count}, maxHours {maxHours}");
                    }

                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Gets the (dis)charging hours for this session.
        /// </summary>
        public int GetHoursForMode()
        {
            int quarters = 0;
            var nextSession = _sessions.GetNextSession(this);
            double power = 0.0;
            var capacity = Mode == Modes.Charging ? _batteryContainer.GetChargingCapacityPerQuarter() : _batteryContainer.GetDischargingCapacityPerQuarter();

            switch (Mode)
            {
                case Modes.Charging:
                    {
                        var previousHourlyInfo = _sessions.GetPreviousHourlyInfo(First!);

                        if (previousHourlyInfo != null)
                        {
                            power = _batteryContainer.GetTotalCapacity() - previousHourlyInfo.ChargeLeft;
                            power = power < 0 ? 0 : power;
                        }
                        else
                        {
                            power = _batteryContainer.GetTotalCapacity();
                        }

                        quarters = (int)Math.Ceiling(power / capacity);

                        break;
                    }

                case Modes.Discharging:
                    {
                        var previousHourlyInfo = _sessions.GetPreviousHourlyInfo(First!);

                        if (previousHourlyInfo != null)
                        {
                            power = previousHourlyInfo.ChargeLeft - First.ChargeNeeded;
                            power = power < 0 ? 0 : power;
                        }

                        quarters = (int)Math.Ceiling(power / capacity);

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Wrong mode for session {this}");
            }


            return quarters;
        }

        /// <summary>
        /// Get the hours needed to charge the batteries to 100%.
        /// </summary>
        internal int GetHours()
        {
            switch (Mode)
            {
                case Modes.Charging:
                    {
                        return SessionHourlyInfos.Count;
                    }

                case Modes.Discharging:
                    {
                        return SessionHourlyInfos.Count;
                    }

                default:
                    throw new InvalidOperationException($"Wrong mode {this}");
            }
        }

        /// <summary>
        /// Are there hourlyInfo items in the session?
        /// </summary>
        public bool IsEmpty() => !SessionHourlyInfos.Any();

        public override string ToString()
        {
            var empty = IsEmpty() ? "!!!" : string.Empty;

            return $"{empty}Session: {Mode}, FirstDate: {FirstDateTime}, LastDate {LastDateTime}, Count: {SessionHourlyInfos.Count}, MaxHours: {MaxQuarters}";
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
