using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;

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
            ZeroNetHome
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

                foreach (var hourlyPrice in QuarterlyInfos)
                {
                    hourlyPrice.SetModes(_mode);
                }
            }
        }

        /// <summary>
        /// The charging power in Watts for (dis)charging.
        /// </summary>
        public double GetChargingPowerInWatts(QuarterlyInfo currentHourlyInfo)
        {
            return currentHourlyInfo.Charging ? _batteryContainer.GetChargingCapacityInWatts() : _batteryContainer.GetDischargingCapacityInWatts();

            // TODO: // This is not correct, we need to calculate the power based on the current hourly info and the mode.
            //var chargeNeeded = currentHourlyInfo.ChargeNeeded;
            //var totalCapacity = _batteryContainer.GetTotalCapacity();
            //var prices = SessionHourlyInfos.Sum(hi => hi.BuyingPrice);
            //var quarterTime = _timeZoneService.Now.DateFloorQuarter();

            //switch (Mode)
            //{
            //    case Modes.Charging:
            //        {
            //            var toCharge = chargeNeeded;
            //            var capacity = _batteryContainer.GetChargingCapacity() / 4.0;

            //            foreach (var quarterlyInfo in SessionHourlyInfos
            //                .Where(hi => hi.Time >= quarterTime)
            //                .OrderBy(hi => hi.BuyingPrice))
            //            {
            //                var watts = Math.Min(capacity, toCharge);
            //                toCharge -= watts;

            //                if (quarterlyInfo.Time == currentHourlyInfo.Time)
            //                {
            //                    return Math.Max(watts, 0.0);
            //                }
            //            }

            //            throw new InvalidOperationException($"Could not find current hourly info for mode {Mode}, HourlyInfo: {currentHourlyInfo}");
            //        }

            //    case Modes.Discharging:
            //        {
            //            var toDischarge = totalCapacity - chargeNeeded;
            //            var capacity = _batteryContainer.GetDischargingCapacity() / 4.0;

            //            foreach (var quarterlyInfo in SessionHourlyInfos
            //                .Where(hi => hi.Time >= quarterTime)
            //                .OrderByDescending(hi => hi.SellingPrice))
            //            {
            //                var watts = Math.Min(capacity, toDischarge);
            //                toDischarge -= watts;

            //                if (quarterlyInfo.Time == currentHourlyInfo.Time)
            //                    return Math.Max(watts, 0.0);
            //            }

            //            throw new InvalidOperationException($"Could not find current hourly info for mode {Mode}, HourlyInfo: {currentHourlyInfo}");
            //        }

            //    default:
            //        throw new InvalidOperationException($"Invalid mode {this}");
            //}
        }

        /// <summary>
        /// Returns true if the quarterlyInfo is contained in the sessions list.
        /// </summary>
        public bool Contains(QuarterlyInfo quarterlyInfo)
        {
            return QuarterlyInfos.Any(hi => hi.Time.DateFloorQuarter() == quarterlyInfo.Time.DateFloorQuarter());
        }

        /// <summary>
        /// All prices in the session
        /// </summary>
        private List<QuarterlyInfo> QuarterlyInfos { get; set; }

        private Sessions _sessions { get; set; }

        private TimeZoneService _timeZoneService { get; set; }

        /// <summary>
        /// Max hours of (dis)charging
        /// </summary>
        public int MaxQuarters { get; set; }

        /// <summary>
        /// The average price of all hourly prices in the session.
        /// </summary>
        public double AveragePrice => QuarterlyInfos.Count > 0 ? QuarterlyInfos.Average(hp => hp.Price) : 0.0;

        /// <summary>
        /// First quarterlyInfo object in the session.
        /// </summary>
        public QuarterlyInfo? First => QuarterlyInfos.OrderBy(hi => hi.Time).FirstOrDefault();

        /// <summary>
        /// Last hourlyinfo object in the session.
        /// </summary>
        public QuarterlyInfo? Last => QuarterlyInfos.OrderByDescending(hi => hi.Time).FirstOrDefault();

        /// <summary>
        /// The first date in the session
        /// </summary>
        public DateTime FirstDateTime => First?.Time ?? DateTime.MinValue;

        /// <summary>
        /// Returns true if any of the prices is negative.
        /// </summary>
        public bool PricesAnyNegative => QuarterlyInfos.Any(hi => hi.Price < 0.0);

        public bool PricesAllPositive => QuarterlyInfos.All(hi => hi.Price >= 0.0);

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
            QuarterlyInfos = new List<QuarterlyInfo>();
            _sessions = sessions;
            _timeZoneService = timeZoneService;
            MaxQuarters = maxQuarters;
            Mode = mode;
            _batteryContainer = batteryContainer;
            _settingsConfig = settingsConfig;
        }

        /// <summary>
        /// Sets the charge needed for each quarterlyInfo object in this session.
        /// </summary>
        public void SetChargeNeeded(double charge)
        {
            QuarterlyInfos.ForEach(hi => hi.SetChargeNeeded(charge));
        }

        /// <summary>
        /// Gets the hourlyInofos list sorted by Time as a readonly collection.
        /// </summary>
        public IReadOnlyCollection<QuarterlyInfo> GetQuarterlyInfoList() => QuarterlyInfos.OrderBy(hi => hi.Time).ToList().AsReadOnly();

        /// <summary>
        /// Add a hourly info object to the list if not already in the list.
        /// </summary>
        public void AddHourlyInfo(QuarterlyInfo quarterlyInfo)
        {
            if (!Contains(quarterlyInfo))
            {
                quarterlyInfo.SetModes(_mode);

                QuarterlyInfos.Add(quarterlyInfo);
            }
        }

        public void RemoveQuarterlyInfo(QuarterlyInfo quarterlyInfo)
        {
            if (Contains(quarterlyInfo))
            {
                DisableChargingAndDischarging(quarterlyInfo);

                QuarterlyInfos.Remove(quarterlyInfo);
            }
        }

        private void DisableChargingAndDischarging(QuarterlyInfo quarterlyInfo)
        {
            quarterlyInfo.DisableCharging();
            quarterlyInfo.DisableDischarging();
        }

        /// <summary>
        /// Returns if this session on average is more profitable.
        /// </summary>
        public bool IsMoreProfitable(Session session)
        {
            if (session.Mode != Mode)
                throw new InvalidOperationException($"Modes should be the same of both sessions {Mode} != {session.Mode}");

            var myAveragePrice = QuarterlyInfos.Average(hi => hi.Price);
            var theirAveragePrice = session.QuarterlyInfos.Average(hi => hi.Price);

            switch (Mode)
            {
                case Modes.Charging:
                    return myAveragePrice <= theirAveragePrice;

                case Modes.Discharging:
                    return myAveragePrice >= theirAveragePrice;

                default:
                    throw new InvalidOperationException($"Wrong mode: {Mode}");
            }
        }

        /// <summary>
        /// Get the cost of this Session.
        /// </summary>
        public double GetTotalCost()
        {
            switch (Mode)
            {
                case Modes.Charging:
                    return QuarterlyInfos.Sum(hi => hi.ChargingCost);

                case Modes.Discharging:
                    return QuarterlyInfos.Sum(hi => hi.DischargingCost);

                default:
                    throw new InvalidOperationException($"Wrong mode: {Mode}");
            }
        }

        /// <summary>
        /// Clear the hourlyInfos from the session.
        /// Caution! Clearing the list will not change the (dis)charging modes on
        /// the quarterlyInfo items unless setModes = true!
        /// </summary>
        /// <param name="setModes">Set to true to change the modes in the listitems.</param>
        public void ClearHourlyInfoList(bool setModes = false)
        {
            if (setModes)
            {
                foreach (var quarterlyInfo in QuarterlyInfos)
                {
                    DisableChargingAndDischarging(quarterlyInfo);
                }
            }

            QuarterlyInfos.Clear();
        }

        public bool RemoveAllAfter(int maxQuarters)
        {
            bool changed = false;

            // Once the session has started don't remove anything before now.
            var now = _timeZoneService.Now.DateFloorQuarter();
            var quarterlyInfos = QuarterlyInfos.Where(hi => hi.Time > now).ToList();

            switch (Mode)
            {
                case Modes.Charging:
                    {
                        // Remove the highest prices
                        var list = quarterlyInfos
                            .OrderByDescending(hi => hi.Price)
                            .ThenBy(hi => hi.Time)
                            .ToList();

                        changed = RemoveTheQuarters(maxQuarters, list);

                        break;
                    }

                case Modes.Discharging:
                    {
                        // Remove the lowest prices
                        var list = quarterlyInfos
                            .OrderBy(hi => hi.Price)
                            .ThenBy(hi => hi.Time)
                            .ToList();

                        changed = RemoveTheQuarters(maxQuarters, list);

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Invalid mode {Mode}");
            }

            return changed;
        }

        private bool RemoveTheQuarters(int maxQuarters, List<QuarterlyInfo> list)
        {
            bool changed = false;
            int count = 0;

            if (maxQuarters <= list.Count)
            {
                count = list.Count - maxQuarters;

                for (int index = 0; index < count; index++)
                {
                    try
                    {
                        RemoveQuarterlyInfo(list[index]);
                    }
                    catch (Exception)
                    {
                        throw new InvalidOperationException($"Index {index}, count {count}, list.Count {list.Count}, maxHours {maxQuarters}");
                    }

                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Gets the (dis)charging hours for this session.
        /// </summary>
        public int GetQuartersForMode()
        {
            int quarters = 0;
            var nextSession = _sessions.GetNextSession(this);
            double power = 0.0;

            switch (Mode)
            {
                case Modes.Charging:
                    {
                        var capacity = _batteryContainer.GetChargingCapacityInWatts() / 4.0; // Per quarter hour.

                        var totalCapacity = _batteryContainer.GetTotalCapacity();

                        if (nextSession != null)
                        {
                            power = totalCapacity - First.ChargeLeft;
                        }
                        else
                        {
                            // This session is the last session
                            power = First.ChargeNeeded - First.ChargeLeft;
                        }

                        power = power < 0 ? 0 : power;

                        quarters = (int)Math.Ceiling(power / capacity);

                        break;
                    }

                case Modes.Discharging:
                    {
                        var capacity = _batteryContainer.GetDischargingCapacityInWatts() / 4.0; // Per quarter hour.

                        power = First.ChargeLeft - First.ChargeNeeded;
                        power = power < 0 ? 0 : power;

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
                        return QuarterlyInfos.Count;
                    }

                case Modes.Discharging:
                    {
                        return QuarterlyInfos.Count;
                    }

                default:
                    throw new InvalidOperationException($"Wrong mode {this}");
            }
        }

        /// <summary>
        /// Are there quarterlyInfo items in the session?
        /// </summary>
        public bool IsEmpty() => !QuarterlyInfos.Any();

        public override string ToString()
        {
            var empty = IsEmpty() ? "!!!" : string.Empty;

            return $"{empty}Session: {Mode}, FirstDate: {FirstDateTime}, LastDate {LastDateTime}, Count: {QuarterlyInfos.Count}, MaxHours: {MaxQuarters}";
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
            }
        }

        internal void Merge(Session nextSession)
        {
            QuarterlyInfos.AddRange(nextSession.QuarterlyInfos);
        }
    }
}
