using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyData.Services;
using System.Collections.ObjectModel;
using System.Threading.Channels;
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

        private VirtualBatteryService _virtualBatteryService { get; set; }

        private ConsumptionDataService _consumptionDataService { get; set; }

        private ConsumptionMonitorService _consumptionMonitorService { get; set; }

        private EnergyHistoryDataService _energyHistoryService { get; set; }

        private List<Session>? _sessionList { get; set; }
        private int _maxChargingQuarters { get; set; }
        private int _maxDischargingQuarters { get; set; }
        private double _cycleCost { get; set; }

        private List<QuarterlyInfo> _quarterlyInfos { get; set; }

        public Sessions(List<QuarterlyInfo> quarterlyInfos,
                        SettingsConfig settingsConfig,
                        BatteryContainer batteryContainer,
                        TimeZoneService? timeZoneService,
                        VirtualBatteryService virtualBatteryService,
                        FinancialResultsService financialResultsService,
                        ConsumptionDataService consumptionDataService,
                        ConsumptionMonitorService consumptionMonitorService,
                        EnergyHistoryDataService energyHistoryService,
                        ILoggerFactory loggerFactory)
        {
            _settingsConfig = settingsConfig;
            _batteryContainer = batteryContainer;
            _financialResultsService = financialResultsService;
            _timeZoneService = timeZoneService;
            _virtualBatteryService = virtualBatteryService;
            _consumptionDataService = consumptionDataService;
            _consumptionMonitorService = consumptionMonitorService;
            _energyHistoryService = energyHistoryService;

            _sessionList = new List<Session>();
            _quarterlyInfos = quarterlyInfos;
            _totalChargingCapacityPerQuarter = batteryContainer.GetChargingCapacityInWattsPerHour() / 4.0; // Per quarter hour.
            _totalDischargingCapacityPerQuarter = batteryContainer.GetDischargingCapacityInWattsPerHour() / 4.0; // Per quarter hour.
            _totalBatteryCapacity = batteryContainer.GetTotalCapacity();
            _maxChargingQuarters = (int)Math.Ceiling(_totalBatteryCapacity / _totalChargingCapacityPerQuarter) + 3; // 3 Quarters marging
            _maxDischargingQuarters = (int)Math.Ceiling(_totalBatteryCapacity / _totalDischargingCapacityPerQuarter) + 3; // 3 Quarters marging
            _cycleCost = settingsConfig.CycleCost;
            _logger = loggerFactory.CreateLogger<Sessions>();

            IdCounter = 0;
        }

        public ReadOnlyCollection<Session> SessionList => _sessionList.AsReadOnly();

        public async Task<decimal> TotalMonthlyCost(DateTime date)
        {
            var queryable = await _financialResultsService.GetFinancialMonthResults(date.Date, date.Date.AddHours(24));
            var list = queryable.ToList();

            if (list.Count == 1)
                return list[0].FinancialResultsList!.Sum(fr => fr.Cost);

            return 0;
        }

        public QuarterlyInfo? GetCurrentQuarterlyInfo()
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
        public void RemoveSession(Session? session, bool resetQuarterlyInfos = true)
        {
            if (session != null)
            {
                if (resetQuarterlyInfos)
                {
                    foreach (var hourlyItem in session.GetQuarterlyInfoList())
                    {
                        hourlyItem.DisableCharging();
                        hourlyItem.DisableDischarging();
                        hourlyItem.ClearSession();
                    }
                }

                if (!_sessionList.Remove(session))
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
            return quarterlyInfo.Charging || quarterlyInfo.Discharging;
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

        private static int IdCounter = 0;

        /// <summary>
        /// Adds a new session to the sessions hourly info list and initializes it.
        /// </summary>
        public Session? AddNewSession(Modes mode, QuarterlyInfo quarterlyInfo)
        {
            Session? session = null;

            if (!InAnySession(quarterlyInfo))
            {
                switch (mode)
                {
                    case Modes.Charging:
                        {
                            session = new Session(this, _timeZoneService!, _virtualBatteryService, mode, _maxChargingQuarters, _batteryContainer, _settingsConfig);
                            session.Id = IdCounter++;
                            _sessionList.Add(session);
                            session.AddQuarterlyInfo(quarterlyInfo);
                            break;
                        }

                    case Modes.Discharging:
                        {
                            session = new Session(this, _timeZoneService!, _virtualBatteryService, mode, _maxDischargingQuarters, _batteryContainer, _settingsConfig);
                            session.Id = IdCounter++;
                            _sessionList.Add(session);
                            session.AddQuarterlyInfo(quarterlyInfo);
                            break;
                        }

                    default:
                        break;
                }
            }
            else
            {
                throw new InvalidOperationException($"Overlap in sessions {quarterlyInfo}");
            }

            return session;
        }

        /// <summary>
        /// Calculates the profits for the hourly info objects in the sessions hourly info list.
        /// </summary>
        public void CalculateProfits(TimeZoneService timeZoneService)
        {
            List<QuarterlyInfo> lastChargingSession = new List<QuarterlyInfo>();

            var localTime = timeZoneService.Now;
            var localTimeHour = localTime.Date.AddHours(localTime.Hour);
            QuarterlyInfo? previousQuarter = null;

            foreach (var nextQuarter in _quarterlyInfos.OrderBy(hi => hi.Time))
            {
                switch (nextQuarter.Mode)
                {
                    case Modes.Charging:
                        CalculateChargingProfits(lastChargingSession, previousQuarter, nextQuarter);
                        break;

                    case Modes.Discharging:
                        CalculateDischargingProfits(nextQuarter);
                        lastChargingSession.Clear();
                        break;

                    case Modes.ZeroNetHome:
                        CalculateZeroNetHomeProfits(lastChargingSession, nextQuarter, true);
                        break;

                    case Modes.Unknown:
                    default:
                        throw new InvalidOperationException($"Wrong mode {nextQuarter.Mode}"); ;
                }

                CalculateZeroNetHomeProfits(lastChargingSession, nextQuarter, false);

                previousQuarter = nextQuarter;
            }
        }

        private void CalculateChargingProfits(List<QuarterlyInfo> lastChargingSession, QuarterlyInfo? previousQuarter, QuarterlyInfo nextQuarter)
        {
            double kWh = GetChargingCapacityInKWh(previousQuarter);

            nextQuarter.Selling = 0.00;
            nextQuarter.Buying = kWh * nextQuarter.Price;

            if (lastChargingSession.Count > 0)
            {
                var lastDateCharging = lastChargingSession.Max(hi => hi.Time);

                if ((nextQuarter.Time - lastDateCharging).Hours > 1)
                {
                    lastChargingSession.Clear();
                }
            }

            lastChargingSession.Add(nextQuarter);
        }

        private double GetChargingCapacityInKWh(QuarterlyInfo? previousHour)
        {
            if (previousHour != null)
            {
                var time = previousHour!.Time;

                var home = _consumptionDataService.GetConsumptionBetween(time, time.AddMinutes(15));
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
            if (lastChargingSession.Count > 0)
            {
                var lastChargingTime = lastChargingSession.Max(lc => lc.Time);
                var hours = (quarterlyInfo.Time - lastChargingTime).Hours;

                if (hours < 10)
                {
                    var quarterlyNeed = quarterlyInfo.EstimatedConsumptionPerQuarterInWatts;
                    var kWh = (Math.Min(quarterlyNeed, quarterlyInfo.ChargeLeft) / 1000); // Per quarter hour.
                    var selling = quarterlyInfo.Price * kWh;
                    var buying = lastChargingSession.Count > 0 ? lastChargingSession.Average(lcs => lcs.Price) * kWh : 0.0;
                    quarterlyInfo.NetZeroHomeProfit = selling - buying;

                    if (save)
                    {
                        quarterlyInfo.Selling = selling;
                        quarterlyInfo.Buying = buying;
                    }

                    return;
                }
            }
            quarterlyInfo.NetZeroHomeProfit = 0.0;

            if (save)
            {
                quarterlyInfo.Selling = 0.0;
                quarterlyInfo.Buying = 0.0;
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
        public bool AddQuarterlyInfo(Session session, QuarterlyInfo quarterlyInfo)
        {
            if (!InAnySession(quarterlyInfo))
            {
                session.AddQuarterlyInfo(quarterlyInfo);
                return true;
            }

            return false;
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
                var homeNeeds = quarterlyInfo.EstimatedConsumptionPerQuarterInWatts;

                if (quarterlyInfo.Time >= first && quarterlyInfo.Time <= last && currentCharge >= 0)
                {
                    if (firstTime)
                    {
                        currentCharge = quarterlyInfo.ChargeLeft;
                        firstTime = false;
                    }

                    hours++;

                    if (quarterlyInfo.ZeroNetHome)
                        currentCharge -= homeNeeds;
                }
            }

            return hours;
        }

        public void CompleteAllSessions()
        {
            foreach (var session in _sessionList!.OrderBy(se => se.FirstDateTime))
            {
                CompleteSession(session);
            }
        }

        public bool RemoveLessProfitableSessions()
        {
            Session? previousSession = null;

            foreach (var nextSession in _sessionList!.OrderBy(se => se.FirstDateTime))
            {
                if (previousSession != null)
                {
                    if (previousSession.Mode == nextSession.Mode)
                    {
                        var previousList = previousSession.GetQuarterlyInfoList();
                        var nextList = nextSession.GetQuarterlyInfoList();

                        switch (previousSession.Mode)
                        {
                            case Modes.Charging:
                                if (previousList.First().MarketPrice > nextList.First().MarketPrice)
                                {
                                    RemoveSession(previousSession);
                                    return true;
                                }
                                else
                                {
                                    RemoveSession(nextSession);
                                    return true;
                                }

                            case Modes.Discharging:
                                if (previousList.First().MarketPrice < nextList.First().MarketPrice)
                                {
                                    RemoveSession(previousSession);
                                    return true;
                                }
                                else
                                {
                                    RemoveSession(nextSession);
                                    return true;
                                }

                            default:
                                break;
                        }
                    }
                }

                previousSession = nextSession;
            }

            return false;
        }

        /// <summary>
        /// Adds neighboring quarters to the Session
        /// </summary>
        public void CompleteSession(Session session)
        {
            if (session.GetQuarterlyInfoList().Count != 1)
                throw new InvalidOperationException($"Session has zero or more than 1 hourly price.");

            var now = _timeZoneService.Now;
            var selectDateQuarter = now.DateFloorQuarter();
            var list = session.GetQuarterlyInfoList();
            var index = _quarterlyInfos.IndexOf(list.First());
            var prev = index - 1;
            var next = index + 1;

            var maxQuarters = session.Mode == Modes.Charging ? _maxChargingQuarters : _maxDischargingQuarters;

            if (index >= 0)
            {
                var minIndex = GetMinIndex(prev);
                var maxIndex = GetMaxIndex(next);

                for (var i = 0; i < maxQuarters; i++)
                {
                    switch (session.Mode)
                    {
                        case Modes.Charging:
                            {
                                if (prev >= minIndex)
                                {
                                    if (next < maxIndex)
                                    {
                                        if (_quarterlyInfos[next].Price < _quarterlyInfos[prev].Price)
                                        {
                                            AddQuarterlyInfo(session, _quarterlyInfos[next++]);
                                        }
                                        else
                                        {
                                            AddQuarterlyInfo(session, _quarterlyInfos[prev--]);
                                        }
                                    }
                                    else
                                    {
                                        AddQuarterlyInfo(session, _quarterlyInfos[prev--]);
                                    }
                                }
                                else
                                {
                                    if (next < maxIndex)
                                    {
                                        AddQuarterlyInfo(session, _quarterlyInfos[next++]);
                                    }
                                }

                                break;
                            }

                        case Modes.Discharging:
                            {
                                if (prev >= 0)
                                {
                                    if (next < maxIndex)
                                    {
                                        if (_quarterlyInfos[next].Price > _quarterlyInfos[prev].Price)
                                        {
                                            AddQuarterlyInfo(session, _quarterlyInfos[next++]);
                                        }
                                        else
                                        {
                                            AddQuarterlyInfo(session, _quarterlyInfos[prev--]);
                                        }
                                    }
                                    else
                                    {
                                        AddQuarterlyInfo(session, _quarterlyInfos[prev--]);
                                    }
                                }
                                else
                                {
                                    if (next < maxIndex)
                                    {
                                        AddQuarterlyInfo(session, _quarterlyInfos[next++]);
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

        private int GetMinIndex(int index)
        {
            while (index >= 0 && !InAnySession(_quarterlyInfos[index]))
            {
                index--;
            }

            return index + 1;
        }

        private int GetMaxIndex(int index)
        {
            while (index < _quarterlyInfos.Count - 1 && !InAnySession(_quarterlyInfos[index]))
            {
                index++;
            }

            return index - 1;
        }

        /// <summary>
        /// Returns the quarterly info objects between 2 sessions 
        /// including the objects of the previous session but excluding the objects of the next session.
        /// </summary>
        private List<QuarterlyInfo> GetInfoObjectsFromStartUntilNextSession(Session previousSession, Session? nextSession = null)
        {
            if (nextSession == null)
            {
                return GetInfoObjectsFromStartToEnd(previousSession);

            }

            return _quarterlyInfos!
                .Where(hi => hi.Time >= previousSession.FirstDateTime && hi.Time < nextSession.FirstDateTime)
                .OrderBy(io => io.Time)
                .ToList();
        }

        /// <summary>
        /// Returns the all quarterly info objects from the start of the session to the end.
        /// </summary>
        private List<QuarterlyInfo> GetInfoObjectsFromStartToEnd(Session session)
        {
            return _quarterlyInfos!
                .Where(hi => hi.Time >= session.FirstDateTime)
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
        /// Returns the single session the quarterly info object is in.
        /// </summary>
        public Session? FindSession(QuarterlyInfo quarterlyInfo)
        {
            List<Session> foundSessions = new List<Session>();

            foreach (var session in SessionList)
            {
                if (session.Contains(quarterlyInfo))
                    foundSessions.Add(session);
            }

            return foundSessions.SingleOrDefault();
        }

        /// <summary>
        /// Merges session2 into session1.
        /// </summary>
        public void MergeSessions(Session session1, Session session2)
        {
            foreach (var quarterlyInfo in session2.GetQuarterlyInfoList())
            {
                session1.AddQuarterlyInfo(quarterlyInfo);
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
            List<Session> sessionsToRemove = new List<Session>();

            foreach (var nextSession in SessionList.OrderBy(se => se.FirstDateTime))
            {
                if (previousSession != null)
                {
                    if (previousSession.Mode == Modes.Charging && nextSession.Mode == Modes.Charging)
                    {
                        if (nextSession.IsMoreProfitable(previousSession))
                        {
                            AddToSessionsToRemove(previousSession, sessionsToRemove);
                        }
                        else
                        {
                            AddToSessionsToRemove(nextSession, sessionsToRemove);
                        }
                    }
                    else if (previousSession.Mode == Modes.Discharging && nextSession.Mode == Modes.Discharging)
                    {
                        if (nextSession.IsMoreProfitable(previousSession))
                        {
                            AddToSessionsToRemove(previousSession, sessionsToRemove);
                        }
                        else
                        {
                            AddToSessionsToRemove(nextSession, sessionsToRemove);
                        }
                    }
                }

                previousSession = nextSession;
            }

            sessionsToRemove.ForEach(se =>
            {
                RemoveSession(se);
                changed = true;
            });

            return changed;
        }

        private static void AddToSessionsToRemove(Session previousSession, List<Session> sessionsToRemove)
        {
            if (!sessionsToRemove.Contains(previousSession))
            {
                sessionsToRemove.Add(previousSession);
            }
        }

        /// <summary>
        /// Is this quarterlyHourInfo within the start end of a session?
        /// </summary>
        public Session? GetSessionTimeFrameOfQuarterlyHour(QuarterlyInfo info)
        {
            foreach (var session in SessionList)
            {
                if (info.Time >= session.FirstDateTime && info.Time <= session.LastDateTime)
                {
                    return session;
                }
            }

            return null;
        }

        /// <summary>
        /// Calculate the charge needed for all Quarterly hour objects and set them.
        /// </summary>
        public void SetEstimateChargeNeededUntilNextSession()
        {
            var margin = 500.0;
            var chargeNeeded = margin;
            QuarterlyInfo? previousQuarterlyHourInfo = null;

            foreach (var quarterlyHour in _quarterlyInfos.OrderByDescending(qi => qi.Time))
            {
                if (previousQuarterlyHourInfo != null)
                {
                    var session = GetSessionTimeFrameOfQuarterlyHour(previousQuarterlyHourInfo);

                    if (session != null)
                    {
                        if (quarterlyHour.Time == session.FirstDateTime.AddMinutes(-15))
                        {
                            // We are at the quarter just before the session.

                            switch (session.Mode)
                            {
                                case Modes.Charging:
                                    // We need to set the charge needed in all quarterlyhours of the session.
                                    session.SetChargeNeeded(chargeNeeded);
                                    chargeNeeded = margin;

                                    break;

                                case Modes.Discharging:
                                    // Calculate the total power needed for this discharging session
                                    chargeNeeded += session.GetTotalPowerRequired();

                                    break;

                                case Modes.ZeroNetHome:
                                case Modes.Unknown:
                                default:
                                    throw new InvalidOperationException($"Wrong mode: {session.Mode}");
                            }
                        }
                    }
                }

                // Add estimated consumption
                chargeNeeded += quarterlyHour.EstimatedConsumptionPerQuarterInWatts;
                // Subtract estimated solar power
                chargeNeeded -= quarterlyHour.SolarPowerPerQuarterInWatts;

                chargeNeeded = EnsureBoundaries(chargeNeeded);

                quarterlyHour.SetChargeNeeded(chargeNeeded);

                previousQuarterlyHourInfo = quarterlyHour;
            }
        }

        /// <summary>
        /// Estimates how much charge is needed in the batteries.
        /// </summary>
        public void SetEstimateChargeNeededUntilNextSessionOld(Session previousSession, Session? nextSession)
        {
            var infoObjects = GetInfoObjectsFromStartUntilNextSession(previousSession, nextSession);

            double chargeNeeded = GetChargeNeeded(previousSession, nextSession);

            foreach (var infoObject in infoObjects)
            {
                switch (previousSession.Mode)
                {
                    case Modes.Charging:
                        chargeNeeded += infoObject.EstimatedConsumptionPerQuarterInWatts;
                        chargeNeeded -= infoObject.SolarPowerPerQuarterInWatts;

                        break;

                    case Modes.Discharging:
                        if (!infoObject.Discharging)
                        {
                            chargeNeeded += infoObject.EstimatedConsumptionPerQuarterInWatts;
                            chargeNeeded -= infoObject.SolarPowerPerQuarterInWatts;
                        }

                        break;
                }
            }

            chargeNeeded = EnsureBoundaries(chargeNeeded);

            foreach (var quarterlyInfo in infoObjects)
            {
                quarterlyInfo.SetChargeNeeded(chargeNeeded);
            }
        }

        /// <summary>
        /// Gets the start value for charge needed.
        /// </summary>
        private double GetChargeNeeded(Session previousSession, Session? nextSession)
        {
            double chargeNeeded = previousSession.Mode == Modes.Charging ? 1000.0 : 500.0; // Margins to compensate for uncertanties. 

            if (nextSession?.Mode == Modes.Discharging)
            {
                var count = nextSession.QuarterlyInfoCount;
                var dischargingCapacityPerQuarterHour = _batteryContainer.GetChargingCapacityInWattsPerQuarter();
                var nextSesseionChargeNeeded = nextSession.AverageChargeNeeded();
                chargeNeeded += count * dischargingCapacityPerQuarterHour + nextSesseionChargeNeeded;
            }

            return chargeNeeded;
        }

        /// <summary>
        /// Ensure the power is positive and less or equal to the total capacity.
        /// </summary>
        private double EnsureBoundaries(double charge)
        {
            var totalCapacity = _batteryContainer.GetTotalCapacity();

            charge = Math.Max(0.0, charge); // Prevent negative power
            charge = Math.Min(charge, totalCapacity); // Prevent charge to be bigger than capacity.

            return charge;
        }

        internal QuarterlyInfo? GetNextQuarterlyInfoInSession(DateTime now)
        {
            var quarterlyInfo = _quarterlyInfos
                .Where(qi => qi.Time > now && (qi.Charging || qi.Discharging))
                .OrderBy(qi => qi.Time)
                .FirstOrDefault();

            return quarterlyInfo;
        }

        internal void CalculateDeltaLowestPrice()
        {
            double lowestPrice = 0.0;

            var sessions = SessionList
                              // .Where(se => se.Mode == Modes.Charging)
                              .OrderBy(se => se.FirstDateTime);

            foreach (var session in sessions)
            {
                var nextSession = GetNextSession(session);

                var objectsBetween = GetInfoObjectsFromStartUntilNextSession(session, nextSession);

                if(session.Mode == Modes.Charging)
                {
                    lowestPrice = session.LowestPrice;
                }

                foreach (var quarterlyInfo in objectsBetween)
                {
                    quarterlyInfo.SetDeltaLowestPrice(lowestPrice);
                }
            }
        }
    }
}
