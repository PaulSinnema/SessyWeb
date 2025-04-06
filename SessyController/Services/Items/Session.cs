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
        /// Returns true if the hourlyInfo is contained in the sessions list.
        /// </summary>
        public bool Contains(HourlyInfo hourlyInfo)
        {
            return SessionHourlyInfos.Any(hi => hi.Time.DateHour() == hourlyInfo.Time.DateHour());
        }

        /// <summary>
        /// All prices in the session
        /// </summary>
        private List<HourlyInfo> SessionHourlyInfos { get; set; }

        private Sessions _sessions { get; set; }

        /// <summary>
        /// Max hours of (dis)charging
        /// </summary>
        public int MaxHours { get; set; }

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
        public DateTime FirstDate => First?.Time ?? DateTime.MinValue;

        /// <summary>
        /// The last date in the session
        /// </summary>
        public DateTime LastDate => Last?.Time ?? DateTime.MaxValue;

        public Session(Sessions sessions,
                       Modes mode,
                       int maxHours,
                       BatteryContainer batteryContainer,
                       SettingsConfig settingsConfig)
        {
            SessionHourlyInfos = new List<HourlyInfo>();
            _sessions = sessions;
            MaxHours = maxHours;
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
            var charge = _batteryContainer.GetChargingCapacity();

            return Math.Min(_batteryContainer.GetTotalCapacity(), hours * charge);
        }

        public double GetDischargeNeeded()
        {
            if (Mode != Modes.Discharging)
                throw new InvalidOperationException($"Invalid mode {this}");

            var hours = GetHours();
            var charge = _batteryContainer.GetDischargingCapacity();

            return hours * charge;
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
        /// Returns true is the average price of this session is cheaper than the session submitted.
        /// </summary>
        public bool IsCheaper(Session session)
        {
            return SessionHourlyInfos.Min(hi => hi.BuyingPrice) < session.SessionHourlyInfos.Min(hi => hi.BuyingPrice);
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

        public bool RemoveAllAfter(int maxHours)
        {
            int index = maxHours;
            bool changed = false;

            switch (Mode)
            {
                case Modes.Charging:
                    {
                        var list = SessionHourlyInfos.OrderBy(hi => hi.BuyingPrice).ToList();

                        while (index < list.Count)
                        {
                            RemoveHourlyInfo(list[index++]);
                            changed = true;
                        }

                        break;
                    }

                case Modes.Discharging:
                    {
                        var list = SessionHourlyInfos.OrderByDescending(hi => hi.SellingPrice).ToList();

                        while (index < list.Count)
                        {
                            RemoveHourlyInfo(list[index++]);
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
        /// Gets the (dis)charging hours for this session.
        /// </summary>
        public int GetHoursForMode()
        {
            int hours = 0;
            var nextSession = _sessions.GetNextSession(this);
            double power = 0.0;

            switch (Mode)
            {
                case Modes.Charging:
                    var previousHourlyInfo = _sessions.GetPreviousHourlyInfo(First!);

                    if (previousHourlyInfo != null)
                    {
                        var chargeLeft = (previousHourlyInfo != null ? previousHourlyInfo.ChargeLeft : 0.0);
                        power = _batteryContainer.GetTotalCapacity() - chargeLeft;
                    }
                    else
                    {
                        power = _batteryContainer.GetTotalCapacity();
                    }
                    break;

                case Modes.Discharging:
                    var neededPower = 0.0;
                    var totalCapacity = _batteryContainer.GetTotalCapacity();

                    if (nextSession != null)
                    {
                        var hourlyInfoObjectsBetween = _sessions.GetInfoObjectsBetween(this, nextSession);
                        neededPower = hourlyInfoObjectsBetween.Count * _settingsConfig.RequiredHomeEnergy / 24;
                    }
                    else
                    {
                        var hourlyInfoObjectsAfter = _sessions.GetInfoObjectsAfter(this);
                        neededPower = hourlyInfoObjectsAfter.Count * _settingsConfig.RequiredHomeEnergy / 24;
                    }

                    power = totalCapacity - neededPower;
                    break;

                default:
                    throw new InvalidOperationException($"Wrong mode for session {this}");
            }

            var capacity = Mode == Modes.Charging ? _batteryContainer.GetChargingCapacity() : _batteryContainer.GetDischargingCapacity();
            hours = (int)Math.Ceiling(power / capacity);

            return hours;
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

            return $"{empty}Session: {Mode}, FirstDate: {FirstDate}, LastDate {LastDate}, Count: {SessionHourlyInfos.Count}, MaxHours: {MaxHours}";
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
