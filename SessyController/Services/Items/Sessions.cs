using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyData.Services;
using System.Collections.ObjectModel;
using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class Sessions : IDisposable
    {
        private double _totalChargingCapacityPerQuarter { get; set; }
        private double _totalDischargingCapacityPerQuarter { get; set; }
        private double _totalBatteryCapacity { get; set; }
        private ILogger<Sessions> _logger { get; set; }
        private SettingsConfig _settingsConfig { get; set; }
        private BatteryContainer _batteryContainer { get; set; }

        private FinancialResultsService _financialResultsService { get; set; }

        private TimeZoneService? _timeZoneService { get; set; }

        private List<Session>? _sessionList { get; set; }
        private int _maxChargingQuarters { get; set; }
        private int _maxDischargingQuarters { get; set; }
        private double _cycleCost { get; set; }

        private List<HourlyInfo> _hourlyInfos { get; set; }

        public Sessions(List<HourlyInfo> hourlyInfos,
                        SettingsConfig settingsConfig,
                        BatteryContainer batteryContainer,
                        TimeZoneService? timeZoneService,
                        FinancialResultsService financialResultsService,
                        ILoggerFactory loggerFactory)
        {
            _settingsConfig = settingsConfig;
            _batteryContainer = batteryContainer;
            _financialResultsService = financialResultsService;
            _timeZoneService = timeZoneService;

            _sessionList = new List<Session>();
            _hourlyInfos = hourlyInfos;
            _totalChargingCapacityPerQuarter = batteryContainer.GetChargingCapacityPerQuarter();
            _totalDischargingCapacityPerQuarter = batteryContainer.GetDischargingCapacityPerQuarter();
            _totalBatteryCapacity = batteryContainer.GetTotalCapacity();
            _maxChargingQuarters = (int)Math.Ceiling(_totalBatteryCapacity / _totalChargingCapacityPerQuarter);
            _maxDischargingQuarters = (int)Math.Ceiling(_totalBatteryCapacity / _totalDischargingCapacityPerQuarter);
            _cycleCost = settingsConfig.CycleCost;
            _logger = loggerFactory.CreateLogger<Sessions>();
        }

        public ReadOnlyCollection<Session> SessionList => _sessionList.AsReadOnly();

        public double TotalRevenue(DateTime date)
        {
            return _hourlyInfos
                    .Where(hi => hi.Time.Date == date.Date)
                    .Sum(hi => hi.Profit);
        }

        public decimal TotalCost(DateTime date)
        {
            var list = _financialResultsService.GetFinancialMonthResults(date.Date, date.Date.AddHours(23));

            if(list.Count == 1)
                return list[0].FinancialResultsList!.Sum(fr => fr.Cost);

            return 0;
        }

        public HourlyInfo? GetCurrentHourlyInfo()
        {
            var localTime = _timeZoneService.Now.DateFloorQuarter();

            return _hourlyInfos?
                .FirstOrDefault(hp => hp.Time == localTime);
        }

        /// <summary>
        /// Removes a session from the sessions session list and changes the modes
        /// of all hourly info objects it contained.
        public void RemoveSession(Session? session)
        {
            if (session != null)
            {
                foreach (var hourlyItem in session.GetHourlyInfoList())
                {
                    hourlyItem.DisableCharging();
                    hourlyItem.DisableDischarging();
                }

                if(!_sessionList.Remove(session))
                {
                    throw new InvalidOperationException($"Could not remove session {session}");
                }
            }
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
        public Session? GetSession(HourlyInfo hourlyInfo)
        {
            return SessionList
                .Where(se => se.Contains(hourlyInfo))
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets the previous hourly info or return null if not found.
        /// </summary>
        public HourlyInfo? GetPreviousHourlyInfo(HourlyInfo currentHourlyInfo)
        {
            return _hourlyInfos
                .Where(hi => hi.Time < currentHourlyInfo.Time)
                .OrderByDescending(hi => hi.Time)
                .FirstOrDefault();
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
        /// Returns the next session for the session.
        /// </summary>
        public Session? GetNextSession(Session session)
        {
            return SessionList
                    .Where(se => se.FirstDateTime > session.LastDateTime)
                    .OrderBy(se => se.FirstDateTime)
                    .FirstOrDefault();
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
                            Session session = new Session(this, _timeZoneService!, mode, _maxChargingQuarters, _batteryContainer, _settingsConfig);
                            _sessionList.Add(session);
                            session.AddHourlyInfo(hourlyInfo);
                            CompleteSession(session, _hourlyInfos, _maxChargingQuarters, _cycleCost);
                            break;
                        }

                    case Modes.Discharging:
                        {
                            Session session = new Session(this, _timeZoneService!, mode, _maxDischargingQuarters, _batteryContainer, _settingsConfig);
                            _sessionList.Add(session);
                            session.AddHourlyInfo(hourlyInfo);
                            CompleteSession(session, _hourlyInfos, _maxDischargingQuarters, _cycleCost);
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
                switch (hourlyInfo.Mode)
                {
                    case Modes.Charging:
                        CalculateChargingProfits(lastChargingSession, previousHour, hourlyInfo);
                        break;

                    case Modes.Discharging:
                        CalculateDischargingProfits(hourlyInfo);
                        break;

                    case Modes.ZeroNetHome:
                        CalculateZeroNetHomeProfits(lastChargingSession, hourlyInfo, true);
                        break;

                    case Modes.Disabled:
                        CalculateDisabledProfits(hourlyInfo);
                        break;

                    case Modes.Unknown:
                    default:
                        throw new InvalidOperationException($"Wrong mode {hourlyInfo.Mode}"); ;
                }

                CalculateZeroNetHomeProfits(lastChargingSession, hourlyInfo, false);

                previousHour = hourlyInfo;
            }
        }

        private void CalculateChargingProfits(List<HourlyInfo> lastChargingSession, HourlyInfo? previousHour, HourlyInfo hourlyInfo)
        {
            var totalChargingCapacity = Math.Min(_totalChargingCapacityPerQuarter, _totalBatteryCapacity - (previousHour == null ? 0.0 : previousHour.ChargeLeft)) / 1000;

            hourlyInfo.Selling = 0.00;
            hourlyInfo.Buying = totalChargingCapacity * hourlyInfo.BuyingPrice;

            if (lastChargingSession.Count > 0)
            {
                var lastDateCharging = lastChargingSession.Max(hi => hi.Time);

                if ((hourlyInfo.Time - lastDateCharging).Hours > 1)
                {
                    lastChargingSession.Clear();
                }
            }

            lastChargingSession.Add(hourlyInfo);
        }

        private void CalculateDischargingProfits(HourlyInfo hourlyInfo)
        {
            var totalDischargingCapacity = Math.Min(_totalDischargingCapacityPerQuarter, hourlyInfo.ChargeLeft - hourlyInfo.ChargeNeeded) / 1000;

            hourlyInfo.Selling = totalDischargingCapacity * hourlyInfo.SellingPrice;
            hourlyInfo.Buying = 0.00;
        }

        /// <summary>
        /// Calculate the profit if NetZeroHome were enabled for this hour.
        /// </summary>
        private void CalculateZeroNetHomeProfits(List<HourlyInfo> lastChargingSession, HourlyInfo hourlyInfo, bool save)
        {
            var kWh = (Math.Min(_settingsConfig.RequiredHomeEnergy / 96, hourlyInfo.ChargeLeft) / 1000); // Per quarter hour.
            var selling = hourlyInfo.SellingPrice * kWh;
            var buying = lastChargingSession.Count > 0 ? lastChargingSession.Average(lcs => lcs.BuyingPrice) * kWh : 0.0;
            hourlyInfo.NetZeroHomeProfit = selling - buying;

            if (save)
            {
                hourlyInfo.Selling = selling;
                hourlyInfo.Buying = buying;
            }
        }

        private static void CalculateDisabledProfits(HourlyInfo hourlyInfo)
        {
            hourlyInfo.Selling = 0.0;
            hourlyInfo.Buying = 0.0;
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
            var homeNeeds = _settingsConfig.RequiredHomeEnergy / 92.0; // Per quarter hour
            double currentCharge = 1.0;

            var first = previousSession.LastDateTime.AddHours(1);
            var last = session.FirstDateTime.AddHours(-1);
            var hours = 0;
            var firstTime = true;

            foreach (var hourlyInfo in _hourlyInfos)
            {
                if (hourlyInfo.Time >= first && hourlyInfo.Time <= last && currentCharge >= 0)
                {
                    if (firstTime)
                    {
                        currentCharge = hourlyInfo.ChargeLeft;
                        firstTime = false;
                    }

                    hours++;

                    if (hourlyInfo.NetZeroHomeWithSolar)
                        currentCharge -= homeNeeds;
                }
            }

            return hours;
        }

        /// <summary>
        /// Adds neighboring hours to the Session
        /// </summary>
        public void CompleteSession(Session session, List<HourlyInfo> hourlyInfos, int maxQuarters, double cycleCost)
        {
            if (session.GetHourlyInfoList().Count != 1)
                throw new InvalidOperationException($"Session has zero or more than 1 hourly price.");

            var now = _timeZoneService.Now;
            var selectDateHour = now.Date.AddHours(now.Hour).AddMinutes(-15);
            var index = hourlyInfos.IndexOf(session.GetHourlyInfoList().First());
            var prev = index - 1;
            var next = index + 1;

            if (index >= 0)
            {
                for (var i = 0; i < maxQuarters - 1; i++)
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
                                        if (hourlyInfos[next].SmoothedPrice < hourlyInfos[prev].SmoothedPrice)
                                        {
                                            if (hourlyInfos[next].SmoothedPrice < averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[next++]);
                                        }
                                        else
                                        {
                                            if (hourlyInfos[prev].SmoothedPrice < averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[prev--]);
                                        }
                                    }
                                    else
                                    {
                                        if (hourlyInfos[prev].SmoothedPrice < averagePrice)
                                            AddHourlyInfo(session, hourlyInfos[prev--]);
                                    }
                                }
                                else
                                {
                                    if (next < hourlyInfos.Count)
                                    {
                                        if (hourlyInfos[next].SmoothedPrice < averagePrice)
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
                                        if (hourlyInfos[next].SmoothedPrice > hourlyInfos[prev].SmoothedPrice)
                                        {
                                            if (hourlyInfos[next].SmoothedPrice > averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[next++]);
                                        }
                                        else
                                        {
                                            if (hourlyInfos[prev].SmoothedPrice > averagePrice)
                                                AddHourlyInfo(session, hourlyInfos[prev--]);
                                        }
                                    }
                                    else
                                    {
                                        if (hourlyInfos[prev].SmoothedPrice > averagePrice)
                                            AddHourlyInfo(session, hourlyInfos[prev--]);
                                    }
                                }
                                else
                                {
                                    if (next < hourlyInfos.Count)
                                    {
                                        if (hourlyInfos[next].SmoothedPrice > averagePrice)
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

        public List<HourlyInfo> GetInfoObjectsBetween(Session previousSession, Session nextSession)
        {
            return _hourlyInfos!
                .Where(hi => hi.Time < nextSession.FirstDateTime && hi.Time > previousSession.LastDateTime)
                .ToList();
        }

        /// <summary>
        /// Gets the hourlyInfo objects between 2 sessions.
        /// </summary>
        /// <summary>
        /// Gets all hourlyInfo objects after the session.
        /// </summary>
        public List<HourlyInfo> GetInfoObjectsAfter(Session session)
        {
            return _hourlyInfos!
                .Where(hi => hi.Time > session.LastDateTime)
                .ToList();
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
                return list.Average(hi => hi.BuyingPrice);

            return hourlyInfos[index].BuyingPrice;
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
            return $"Sessions: Count: {SessionList.Count}, Max charging hours: {_maxChargingQuarters}, Max discharging hours: {_maxDischargingQuarters}";
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

        internal bool RemoveMoreExpensiveChargingSessions()
        {
            var changed = false;
            Session? previousSession = null;

            foreach (var session in SessionList.OrderBy(se => se.FirstDateTime).ToList())
            {
                if (previousSession != null)
                {
                    if (previousSession.Mode == Modes.Charging && session.Mode == Modes.Charging)
                    {
                        if (session.IsMoreProfitable(previousSession))
                        {
                            RemoveSession(previousSession);
                            changed = true;
                        }
                    }
                }

                previousSession = session;
            }

            return changed;
        }
    }
}
