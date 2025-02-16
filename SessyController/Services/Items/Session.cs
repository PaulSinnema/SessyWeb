﻿using SessyController.Configurations;

namespace SessyController.Services.Items
{
    /// <summary>
    /// (Dis)charging session
    /// </summary>
    public class Session
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

        public override string ToString()
        {
            return $"Session: {Mode}, FirstDate: {FirstDate}, LastDate {LastDate}, Count: {HourlyInfos.Count}, MaxHours: {MaxHours}";
        }

        /// <summary>
        /// Get the hours needed to charge the batteries to 100%.
        /// </summary>
        internal int GetChargingHours()
        {
            if (First != null)
            {
                var index = HourlyInfos.IndexOf(First);

                if (index > 0)
                {
                    var chargeLeft = HourlyInfos[index - 1].ChargeLeft;
                    var toCharge = _batteryContainer.GetTotalCapacity() - chargeLeft;

                    var chargingHours = (int)Math.Ceiling(toCharge / _batteryContainer.GetChargingCapacity());

                    return chargingHours;
                }
            }

            return MaxHours;
        }
    }
}
