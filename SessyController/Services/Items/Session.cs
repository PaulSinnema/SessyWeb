﻿using SessyCommon.Extensions;
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

                foreach (var hourlyPrice in HourlyInfos)
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
            return HourlyInfos.Any(hi => hi.Time.DateHour() == hourlyInfo.Time.DateHour());
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
        public double AveragePrice => HourlyInfos.Count > 0 ? HourlyInfos.Average(hp => hp.BuyingPrice) : 0.0;

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
        /// The min this session must leave in the batteries during discharge.
        /// </summary>
        public double MinChargeNeeded { get; set; }

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
        /// Add a hourly info object to the list if not already in the list.
        /// </summary>
        public void AddHourlyInfo(HourlyInfo hourlyInfo)
        {
            if (!Contains(hourlyInfo))
            {
                hourlyInfo.SetModes(_mode);

                HourlyInfos.Add(hourlyInfo);
            }
        }

        public void RemoveHourlyInfo(HourlyInfo hourlyInfo)
        {
            if (Contains(hourlyInfo))
            {
                DisableChargingAndDischarging(hourlyInfo);

                HourlyInfos.Remove(hourlyInfo);
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
            return HourlyInfos.Min(hi => hi.BuyingPrice) < session.HourlyInfos.Min(hi => hi.BuyingPrice);
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

        public bool RemoveAllAfter(int maxHours)
        {
            int index = maxHours;
            bool changed = false;

            switch (Mode)
            {
                case Modes.Charging:
                    {
                        var list = HourlyInfos.OrderBy(hi => hi.BuyingPrice).ToList();

                        while (++index < list.Count)
                        {
                            RemoveHourlyInfo(list[index]);
                            changed = true;
                        }

                        break;
                    }

                case Modes.Discharging:
                    {
                        var list = HourlyInfos.OrderByDescending(hi => hi.BuyingPrice).ToList();

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

            if (!IsEmpty())
            {
                var chargeNeeded = MaxChargeNeeded - HourlyInfos.Average(hi => hi.ChargeLeft);

                if (chargeNeeded >= 0)
                    return (int)Math.Ceiling(chargeNeeded / _batteryContainer.GetChargingCapacity());
            }

            return 0;
        }

        /// <summary>
        /// Are there hourlyInfo items in the session?
        /// </summary>
        public bool IsEmpty() => !HourlyInfos.Any();

        public override string ToString()
        {
            var empty = IsEmpty() ? "!!!" : string.Empty;

            return $"{empty}Session: {Mode}, FirstDate: {FirstDate}, LastDate {LastDate}, Count: {HourlyInfos.Count}, MaxChargeNeeded: {MaxChargeNeeded}, MaxHours: {MaxHours}";
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
