using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
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

        private ConsumptionDataService _consumptionDataService { get; set; }

        private ConsumptionMonitorService _consumptionMonitorService { get; set; }

        private EnergyHistoryService _energyHistoryService { get; set; }

        private List<Session>? _sessionList { get; set; }
        private int _maxChargingQuarters { get; set; }
        private int _maxDischargingQuarters { get; set; }
        private double _cycleCost { get; set; }

        private List<QuarterlyInfo> _quarterlyInfos { get; set; }

        public Sessions(List<QuarterlyInfo> hourlyInfos,
                        SettingsConfig settingsConfig,
                        BatteryContainer batteryContainer,
                        TimeZoneService? timeZoneService,
                        FinancialResultsService financialResultsService,
                        ConsumptionDataService consumptionDataService,
                        ConsumptionMonitorService consumptionMonitorService,
                        EnergyHistoryService energyHistoryService,
                        ILoggerFactory loggerFactory)
        {
            _settingsConfig = settingsConfig;
            _batteryContainer = batteryContainer;
            _financialResultsService = financialResultsService;
            _timeZoneService = timeZoneService;
            _consumptionDataService = consumptionDataService;
            _consumptionMonitorService = consumptionMonitorService;
            _energyHistoryService = energyHistoryService;


            _sessionList = new List<Session>();
            _quarterlyInfos = hourlyInfos;
            _totalChargingCapacityPerQuarter = batteryContainer.GetChargingCapacityInWatts() / 4.0; // Per quarter hour.
            _totalDischargingCapacityPerQuarter = batteryContainer.GetDischargingCapacityInWatts() / 4.0; // Per quarter hour.
            _totalBatteryCapacity = batteryContainer.GetTotalCapacity();
            _maxChargingQuarters = (int)Math.Ceiling(_totalBatteryCapacity / _totalChargingCapacityPerQuarter);
            _maxDischargingQuarters = (int)Math.Ceiling(_totalBatteryCapacity / _totalDischargingCapacityPerQuarter);
            _cycleCost = settingsConfig.CycleCost;
            _logger = loggerFactory.CreateLogger<Sessions>();
        }

        public ReadOnlyCollection<Session> SessionList => _sessionList.AsReadOnly();

        public double TotalRevenue(DateTime date)
        {
            return _quarterlyInfos
                    .Where(hi => hi.Time.Date == date.Date)
                    .Sum(hi => hi.Profit);
        }

        public async Task<decimal> TotalCost(DateTime date)
        {
            var queryable = await _financialResultsService.GetFinancialMonthResults(date.Date, date.Date.AddHours(24));
            var list = queryable.ToList();

            if (list.Count == 1)
                return list[0].FinancialResultsList!.Sum(fr => fr.Cost);

            return 0;
        }

        public QuarterlyInfo? GetCurrentHourlyInfo()
        {
            var localTime = _timeZoneService.Now.DateFloorQuarter();

            var quarterlyInfo = _quarterlyInfos?
                .FirstOrDefault(hp => hp.Time == localTime);

            if (quarterlyInfo == null)
                _logger.LogWarning($"Hourly info for {localTime} not found in hourly info list.");

            return quarterlyInfo;
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
        public bool InAnySession(QuarterlyInfo quarterlyInfo)
        {
            foreach (var se in _sessionList)
            {
                if (se.Contains(quarterlyInfo))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Return the session that contains the hourly info.
        /// </summary>
        public Session? GetSession(QuarterlyInfo quarterlyInfo)
        {
            return SessionList
                .Where(se => se.Contains(quarterlyInfo))
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets the previous hourly info or return null if not found.
        /// </summary>
        public QuarterlyInfo? GetPreviousHourlyInfo(QuarterlyInfo currentHourlyInfo)
        {
            return _quarterlyInfos
                .Where(hi => hi.Time < currentHourlyInfo.Time)
                .OrderByDescending(hi => hi.Time)
                .FirstOrDefault();
        }

        /// <summary>
        /// Removes the hourly info object from the session and chnages its charging mode.
        /// </summary>
        public void RemoveFromSession(QuarterlyInfo quarterlyInfo)
        {
            foreach (var session in _sessionList)
            {
                if (session.Contains(quarterlyInfo))
                {
                    session.RemoveQuarterlyInfo(quarterlyInfo);
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
        public void AddNewSession(Modes mode, QuarterlyInfo quarterlyInfo)
        {
            if (!InAnySession(quarterlyInfo))
            {
                switch (mode)
                {
                    case Modes.Charging:
                        {
                            Session session = new Session(this, _timeZoneService!, mode, _maxChargingQuarters, _batteryContainer, _settingsConfig);
                            _sessionList.Add(session);
                            session.AddHourlyInfo(quarterlyInfo);
                            CompleteSession(session, _quarterlyInfos, _maxChargingQuarters, _cycleCost);
                            break;
                        }

                    case Modes.Discharging:
                        {
                            Session session = new Session(this, _timeZoneService!, mode, _maxDischargingQuarters, _batteryContainer, _settingsConfig);
                            _sessionList.Add(session);
                            session.AddHourlyInfo(quarterlyInfo);
                            CompleteSession(session, _quarterlyInfos, _maxDischargingQuarters, _cycleCost);
                            break;
                        }

                    default:
                        break;
                }
            }
            else
                _logger.LogInformation($"Overlap in sessions {quarterlyInfo}");
        }

        /// <summary>
        /// Calculates the profits for the hourly info objects in the sessions hourly info list.
        /// </summary>
        public void CalculateProfits(TimeZoneService timeZoneService)
        {
            List<QuarterlyInfo> lastChargingSession = new List<QuarterlyInfo>();

            var localTime = timeZoneService.Now;
            var localTimeHour = localTime.Date.AddHours(localTime.Hour);
            QuarterlyInfo? previousHour = null;

            foreach (var quarterlyInfo in _quarterlyInfos
                .OrderBy(hi => hi.Time))
            {
                switch (quarterlyInfo.Mode)
                {
                    case Modes.Charging:
                        CalculateChargingProfits(lastChargingSession, previousHour, quarterlyInfo);
                        break;

                    case Modes.Discharging:
                        CalculateDischargingProfits(quarterlyInfo);
                        break;

                    case Modes.ZeroNetHome:
                        CalculateZeroNetHomeProfits(lastChargingSession, quarterlyInfo, true);
                        break;

                    case Modes.Disabled:
                        CalculateDisabledProfits(quarterlyInfo);
                        break;

                    case Modes.Unknown:
                    default:
                        throw new InvalidOperationException($"Wrong mode {quarterlyInfo.Mode}"); ;
                }

                CalculateZeroNetHomeProfits(lastChargingSession, quarterlyInfo, false);

                previousHour = quarterlyInfo;
            }
        }

        private void CalculateChargingProfits(List<QuarterlyInfo> lastChargingSession, QuarterlyInfo? previousQuarter, QuarterlyInfo quarterlyInfo)
        {
            double kWh = GetChargingCapacityInKWh(previousQuarter);

            quarterlyInfo.Selling = 0.00;
            quarterlyInfo.Buying = kWh * quarterlyInfo.Price;

            if (lastChargingSession.Count > 0)
            {
                var lastDateCharging = lastChargingSession.Max(hi => hi.Time);

                if ((quarterlyInfo.Time - lastDateCharging).Hours > 1)
                {
                    lastChargingSession.Clear();
                }
            }

            lastChargingSession.Add(quarterlyInfo);
        }

        private double GetChargingCapacityInKWh(QuarterlyInfo? previousHour)
        {
            if (previousHour != null)
            {
                var time = previousHour!.Time;

                var home = _consumptionDataService.GetConsumptionBetween(time.AddMinutes(-15), time);
                var net = _energyHistoryService.GetNetPowerBetween(time, time.AddMinutes(15));

                if (!home.noData && !net.noData)
                {
                    return -((net.watts + home.watts) / 1000);
                }
            }

            var capacity = Math.Min(_totalChargingCapacityPerQuarter, _totalBatteryCapacity - (previousHour == null ? 0.0 : previousHour.ChargeLeft)) / 1000;

            return capacity;
        }

        private void CalculateDischargingProfits(QuarterlyInfo quarterlyInfo)
        {
            var totalDischargingCapacity = Math.Min(_totalDischargingCapacityPerQuarter, quarterlyInfo.ChargeLeft - quarterlyInfo.ChargeNeeded) / 1000;

            quarterlyInfo.Selling = totalDischargingCapacity * quarterlyInfo.Price;
            quarterlyInfo.Buying = 0.00;
        }

        /// <summary>
        /// Calculate the profit if NetZeroHome were enabled for this hour.
        /// </summary>
        private void CalculateZeroNetHomeProfits(List<QuarterlyInfo> lastChargingSession, QuarterlyInfo quarterlyInfo, bool save)
        {
            var quarterlyNeed = quarterlyInfo.EstimatedConsumptionPerQuarterHour;
            var kWh = (Math.Min(quarterlyNeed, quarterlyInfo.ChargeLeft) / 1000); // Per quarter hour.
            var selling = quarterlyInfo.Price * kWh;
            var buying = lastChargingSession.Count > 0 ? lastChargingSession.Average(lcs => lcs.Price) * kWh : 0.0;
            quarterlyInfo.NetZeroHomeProfit = selling - buying;

            if (save)
            {
                quarterlyInfo.Selling = selling;
                quarterlyInfo.Buying = buying;
            }
        }

        private static void CalculateDisabledProfits(QuarterlyInfo quarterlyInfo)
        {
            quarterlyInfo.Selling = 0.0;
            quarterlyInfo.Buying = 0.0;
        }

        /// <summary>
        /// Adds an hourly info object to the sessions hourly info list if it is not contained in
        /// any other session.
        /// </summary>
        public void AddHourlyInfo(Session session, QuarterlyInfo quarterlyInfo)
        {
            if (!InAnySession(quarterlyInfo))
                session.AddHourlyInfo(quarterlyInfo);
        }

        public double GetMaxZeroNetHomeHours(Session previousSession, Session session)
        {
            double currentCharge = 1.0;

            var first = previousSession.LastDateTime.AddHours(1);
            var last = session.FirstDateTime.AddHours(-1);
            var hours = 0;
            var firstTime = true;

            foreach (var quarterlyInfo in _quarterlyInfos)
            {
                var homeNeeds = quarterlyInfo.EstimatedConsumptionPerQuarterHour;

                if (quarterlyInfo.Time >= first && quarterlyInfo.Time <= last && currentCharge >= 0)
                {
                    if (firstTime)
                    {
                        currentCharge = quarterlyInfo.ChargeLeft;
                        firstTime = false;
                    }

                    hours++;

                    if (quarterlyInfo.NetZeroHomeWithSolar)
                        currentCharge -= homeNeeds;
                }
            }

            return hours;
        }

        /// <summary>
        /// Adds neighboring hours to the Session
        /// </summary>
        public void CompleteSession(Session session, List<QuarterlyInfo> hourlyInfos, int maxQuarters, double cycleCost)
        {
            if (session.GetHourlyInfoList().Count != 1)
                throw new InvalidOperationException($"Session has zero or more than 1 hourly price.");

            var now = _timeZoneService.Now;
            var selectDateHour = now.Date.AddHours(now.Hour).AddMinutes(-15);
            var list = session.GetHourlyInfoList();
            var index = hourlyInfos.IndexOf(list.First());
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

        /// <summary>
        /// Returns the hourly info objects between 2 sessions.
        /// </summary>
        public List<QuarterlyInfo> GetInfoObjectsBetween(Session previousSession, Session nextSession)
        {
            return _quarterlyInfos!
                .Where(hi => hi.Time < nextSession.FirstDateTime && hi.Time > previousSession.LastDateTime)
                .ToList();
        }

        /// <summary>
        /// Returns the hourly info objects after the session and before the date.
        /// </summary>
        public List<QuarterlyInfo> GetInfoObjectsAfter(Session session)
        {
            return _quarterlyInfos!
                .Where(hi => hi.Time > session.LastDateTime)
                .ToList();
        }


        /// <summary>
        /// Gets the average price for prices inside the window of the current hourly info.
        /// </summary>
        private double GetAveragePriceInWindow(List<QuarterlyInfo> hourlyInfos, int index, int window = 10)
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
        public Session FindSession(QuarterlyInfo quarterlyInfo)
        {
            List<Session> foundSessions = new List<Session>();

            foreach (var session in SessionList)
            {
                if (session.Contains(quarterlyInfo))
                    foundSessions.Add(session);
            }

            return foundSessions.Single();
        }

        /// <summary>
        /// Merges session2 into session1.
        /// </summary>
        public void MergeSessions(Session session1, Session session2)
        {
            foreach (var quarterlyInfo in session2.GetHourlyInfoList())
            {
                session1.AddHourlyInfo(quarterlyInfo);
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
                    } else if(previousSession.Mode == Modes.Discharging && session.Mode == Modes.Discharging)
                    {
                        if(session.IsMoreProfitable(previousSession))
                        {
                            RemoveSession(session);
                            changed = true;
                        }
                    } else if(previousSession.Mode == Modes.Charging && session.Mode == Modes.Discharging)
                    {
                        var chargeCost = previousSession.GetTotalCost();
                        var dischargeCost = session.GetTotalCost();

                        if(chargeCost > dischargeCost)
                        {
                            RemoveSession(session);
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
