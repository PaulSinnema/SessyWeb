using SessyCommon.Extensions;
using SessyController.Configurations;
using System.Collections.ObjectModel;
using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class Sessions : IDisposable
    {
        private double _totalChargingCapacity { get; set; }
        private double _totalDischargingCapacity { get; set; }
        private double _homeNeeds { get; set; }
        private double _totalBatteryCapacity { get; set; }
        private double _netZeroHomeMinProfit { get; set; }
        private ILogger<Sessions> _logger { get; set; }
        private SettingsConfig _settingsConfig { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private TimeZoneService? _timeZoneService { get; set; }

        private List<Session>? _sessionList { get; set; }
        private int _maxChargingHours { get; set; }
        private int _maxDischargingHours { get; set; }
        private double _cycleCost { get; set; }

        private List<HourlyInfo> _hourlyInfos { get; set; }

        public Sessions(List<HourlyInfo> hourlyInfos,
                        SettingsConfig settingsConfig,
                        BatteryContainer batteryContainer,
                        TimeZoneService? timeZoneService,
                        ILoggerFactory loggerFactory)
        {
            _settingsConfig = settingsConfig;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;

            _sessionList = new List<Session>();
            _hourlyInfos = hourlyInfos;
            _totalChargingCapacity = batteryContainer.GetChargingCapacity();
            _totalDischargingCapacity = batteryContainer.GetDischargingCapacity();
            _totalBatteryCapacity = batteryContainer.GetTotalCapacity();
            _maxChargingHours = (int)Math.Ceiling(_totalBatteryCapacity / _totalChargingCapacity);
            _maxDischargingHours = (int)Math.Ceiling(_totalBatteryCapacity / _totalDischargingCapacity);
            _cycleCost = settingsConfig.CycleCost;
            _homeNeeds = settingsConfig.RequiredHomeEnergy;
            _netZeroHomeMinProfit = settingsConfig.NetZeroHomeMinProfit;
            _logger = loggerFactory.CreateLogger<Sessions>();
        }

        public ReadOnlyCollection<Session> SessionList => _sessionList.AsReadOnly();

        /// <summary>
        /// Removes a session from the sessions session list and changes the modes
        /// of all hourly info objects it contained.
        public void RemoveSession(Session session)
        {
            foreach (var hourlyItem in session.GetHourlyInfoList())
            {
                hourlyItem.DisableCharging();
                hourlyItem.DisableDischarging();
            }

            _sessionList.Remove(session);
        }

        /// <summary>
        /// Returns true if the hourly info object is in any session.
        /// </summary>
        public bool InAnySession(HourlyInfo hourlyInfo)
        {
            foreach (var se in _sessionList)
            {
                if (se.Contains(hourlyInfo))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Return the session that contains the hourly info.
        /// </summary>
        public Session GetSession(HourlyInfo hourlyInfo)
        {
            return SessionList
                .Where(se => se.Contains(hourlyInfo))
                .First();
        }

        /// <summary>
        /// Removes the hourly info object from the session and chnages its charging mode.
        /// </summary>
        public void RemoveFromSession(HourlyInfo hourlyInfo)
        {
            foreach (var session in _sessionList)
            {
                if (session.Contains(hourlyInfo))
                {
                    session.RemoveHourlyInfo(hourlyInfo);
                }
            }
        }

        /// <summary>
        /// Adds a new session to the sessions hourly info list and initializes it.
        /// </summary>
        public void AddNewSession(Modes mode, HourlyInfo hourlyInfo)
        {
            if (!InAnySession(hourlyInfo))
            {
                switch (mode)
                {
                    case Modes.Charging:
                        {
                            Session session = new Session(mode, _maxChargingHours, _batteryContainer, _settingsConfig);
                            _sessionList.Add(session);
                            session.AddHourlyInfo(hourlyInfo);
                            CompleteSession(session, _hourlyInfos, _maxChargingHours, _cycleCost);
                            break;
                        }

                    case Modes.Discharging:
                        {
                            Session session = new Session(mode, _maxDischargingHours, _batteryContainer, _settingsConfig);
                            _sessionList.Add(session);
                            session.AddHourlyInfo(hourlyInfo);
                            CompleteSession(session, _hourlyInfos, _maxDischargingHours, _cycleCost);
                            break;
                        }

                    default:
                        break;
                }
            }
            else
                _logger.LogInformation($"Overlap in sessions {hourlyInfo}");
        }

        /// <summary>
        /// Calculates the profits for the hourly info objects in the sessions hourly info list.
        /// </summary>
        public void CalculateProfits(TimeZoneService timeZoneService)
        {
            List<HourlyInfo> lastChargingSession = new List<HourlyInfo>();

            var localTime = timeZoneService.Now;
            var localTimeHour = localTime.Date.AddHours(localTime.Hour);
            HourlyInfo? previousHour = null;

            foreach (var hourlyInfo in _hourlyInfos
                // .Where(hp => hp.Time.Date.AddHours(hp.Time.Hour) >= localTimeHour)
                .OrderBy(hi => hi.Time))
            {
                switch ((hourlyInfo.Charging, hourlyInfo.Discharging, hourlyInfo.ZeroNetHome))
                {
                    case (true, false, false): // Charging
                        {
                            var totalChargingCapacity = Math.Min(_totalChargingCapacity, _totalBatteryCapacity - (previousHour == null ? 0.0 : previousHour.ChargeLeft)) / 1000;

                            hourlyInfo.Selling = 0.00;
                            hourlyInfo.Buying = totalChargingCapacity * hourlyInfo.Price;

                            if (lastChargingSession.Count > 0)
                            {
                                var lastDateCharging = lastChargingSession.Max(hi => hi.Time);

                                if (hourlyInfo.Time.Hour - lastDateCharging.Hour > 1)
                                {
                                    lastChargingSession.Clear();
                                }
                            }

                            lastChargingSession.Add(hourlyInfo);
                            break;
                        }

                    case (false, true, false): // Discharging
                        {
                            var totalDischargingCapacity = Math.Min(_totalDischargingCapacity, hourlyInfo.ChargeLeft) / 1000;

                            hourlyInfo.Selling = totalDischargingCapacity * hourlyInfo.Price;
                            hourlyInfo.Buying = 0.00;
                            break;
                        }

                    case (false, false, true): // Zero net home
                        {
                            var kWh = Math.Min(_homeNeeds / 24, hourlyInfo.ChargeLeft) / 1000;
                            hourlyInfo.Selling = hourlyInfo.Price * kWh;
                            hourlyInfo.Buying = lastChargingSession.Count > 0 ? lastChargingSession.Average(lcs => lcs.Price) * kWh : 0.0;
                            break;
                        }

                    case (false, false, false): // Disabled
                        {
                            hourlyInfo.Selling = 0.0;
                            hourlyInfo.Buying = 0.0;

                            break;
                        }

                    default:
                        {
                            throw new InvalidOperationException("Wrong combination of Booleans");
                        }
                }

                previousHour = hourlyInfo;
            }
        }

        /// <summary>
        /// Adds an hourly info object to the sessions hourly info list if it is not contained in
        /// any other session.
        /// </summary>
        public void AddHourlyInfo(Session session, HourlyInfo hourlyInfo)
        {
            if (!InAnySession(hourlyInfo))
                session.AddHourlyInfo(hourlyInfo);
        }

        public double GetMaxZeroNetHomeHours(Session previousSession, Session session)
        {
            var homeNeeds = _settingsConfig.RequiredHomeEnergy / 24.0;
            double currentCharge = 1.0; // await _batteryContainer.GetStateOfChargeInWatts();

            var first = previousSession.LastDate.AddHours(1);
            var last = session.FirstDate.AddHours(-1);
            var hours = 0;
            var firstTime = true;

            foreach (var hourlyInfo in _hourlyInfos)
            {
                if(hourlyInfo.Time >= first && hourlyInfo.Time <= last && currentCharge >= 0)
                {
                    if(firstTime)
                    {
                        currentCharge = hourlyInfo.ChargeLeft;
                        firstTime = false;
                    }

                    hours++;

                    if(hourlyInfo.ZeroNetHome)
                        currentCharge -= homeNeeds;
                }
            }

            return hours;
        }

        /// <summary>
        /// Adds neighboring hours to the Session
        /// </summary>
        public void CompleteSession(Session session, List<HourlyInfo> hourlyInfos, int maxHours, double cycleCost)
        {
            if (session.GetHourlyInfoList().Count != 1)
                throw new InvalidOperationException($"Session has zero or more than 1 hourly price.");

            var now = _timeZoneService.Now;
            var selectDateHour = now.Date.AddHours(now.Hour).AddHours(-1);
            var index = hourlyInfos.IndexOf(session.GetHourlyInfoList().First());
            var prev = index - 1;
            var next = index + 1;

            if (index >= 0)
            { 
                for (var i = 0; i < maxHours - 1; i++)
                {
                    var averagePrice = GetAveragePriceInWindow(hourlyInfos, index, 20);

                    switch (session.Mode)
                    {
                        case Modes.Charging:
                            {
                                if (prev >= 0)
                                {
                                    if (next < hourlyInfos.Count)
                                    {
                                        if (hourlyInfos[next].Price < hourlyInfos[prev].Price)
                                        {
                                            if (hourlyInfos[next].Price < averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[next++]);
                                        }
                                        else
                                        {
                                            if (hourlyInfos[prev].Price < averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[prev--]);
                                        }
                                    }
                                    else
                                    {
                                        if (hourlyInfos[prev].Price < averagePrice)
                                            AddHourlyInfo(session, hourlyInfos[prev--]);
                                    }
                                }
                                else
                                {
                                    if (next < hourlyInfos.Count)
                                    {
                                        if (hourlyInfos[next].Price < averagePrice)
                                            AddHourlyInfo(session, hourlyInfos[next++]);
                                    }
                                }

                                break;
                            }

                        case Modes.Discharging:
                            {
                                if (prev >= 0)
                                {
                                    if (next < hourlyInfos.Count)
                                    {
                                        if (hourlyInfos[next].Price > hourlyInfos[prev].Price)
                                        {
                                            if (hourlyInfos[next].Price > averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[next++]);
                                        }
                                        else
                                        {
                                            if (hourlyInfos[prev].Price > averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[prev--]);
                                        }
                                    }
                                    else
                                    {
                                        if (hourlyInfos[prev].Price > averagePrice)
                                            AddHourlyInfo(session, hourlyInfos[prev--]);
                                    }
                                }
                                else
                                {
                                    if (next < hourlyInfos.Count)
                                    {
                                        if (hourlyInfos[next].Price > averagePrice)
                                            AddHourlyInfo(session, hourlyInfos[next++]);
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

        /// <summary>
        /// Gets the average price for prices inside the window of the current hourly info.
        /// </summary>
        private double GetAveragePriceInWindow(List<HourlyInfo> hourlyInfos, int index, int window = 10)
        {
            var halfWindow = window / 2;
            var start = index - halfWindow;
            var end = index + halfWindow;

            if (start < 0) start = 0;
            if (end > hourlyInfos.Count) end = hourlyInfos.Count - 1;

            var list = hourlyInfos
                .OrderBy(hi => hi.Time)
                .ToList()
                .GetRange(start, end - start);

            if (list.Count > 0)
                return list.Average(hi => hi.Price);

            return hourlyInfos[index].Price;
        }

        /// <summary>
        /// Returns the single session the hourly info object is in.
        /// </summary>
        public Session FindSession(HourlyInfo hourlyInfo)
        {
            List<Session> foundSessions = new List<Session>();

            foreach (var session in SessionList)
            {
                if (session.Contains(hourlyInfo))
                    foundSessions.Add(session);
            }

            return foundSessions.Single();
        }

        /// <summary>
        /// Merges session2 into session1.
        /// </summary>
        public void MergeSessions(Session session1, Session session2)
        {
            foreach (var hourlyInfo in session2.GetHourlyInfoList())
            {
                session1.AddHourlyInfo(hourlyInfo);
            }

            session2.ClearHourlyInfoList();
        }

        public override string ToString()
        {
            return $"Sessions: Count: {SessionList.Count}, Max charging hours: {_maxChargingHours}, Max discharging hours: {_maxDischargingHours}";
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _sessionList.Clear();
                _sessionList = null;
                _isDisposed = true;
            }
        }
    }
}
