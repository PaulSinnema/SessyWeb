﻿using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class Sessions
    {
        private double _totalChargingCapacity { get; set; }
        private double _totalDischargingCapacity { get; set; }
        private double _homeNeeds { get; set; }
        private double _totalBatteryCapacity { get; set; }

        private ILogger<Sessions> _logger;

        private List<Session> _sessionList { get; set; }
        private int _maxChargingHours { get; set; }
        private int _maxDischargingHours { get; set; }
        private double _cycleCost { get; set; }

        private List<HourlyInfo> _hourlyInfos { get; set; }

        public Sessions(List<HourlyInfo> hourlyInfos,
                        int maxChargingHours,
                        int maxDischargingHours,
                        double totalChargingCapacity,
                        double totalDischargingCapacity,
                        double totalBatteryCapacity,
                        double homeNeeds,
                        double cycleCost,
                        ILoggerFactory loggerFactory)
        {
            _sessionList = new List<Session>();
            _hourlyInfos = hourlyInfos;
            _maxChargingHours = maxChargingHours;
            _maxDischargingHours = maxDischargingHours;
            _cycleCost = cycleCost;
            _totalChargingCapacity = totalChargingCapacity;
            _totalDischargingCapacity = totalDischargingCapacity;
            _homeNeeds = homeNeeds;
            _totalBatteryCapacity = totalBatteryCapacity;
            _logger = loggerFactory.CreateLogger<Sessions>();
        }

        public List<Session> SessionList => _sessionList;

        public bool InAnySession(HourlyInfo hourlyInfo)
        {
            foreach (var se in _sessionList)
            {
                if (se.PriceList.Contains(hourlyInfo))
                    return true;
            }

            return false;
        }

        public void AddNewSession(Modes mode, HourlyInfo hourlyInfo, double averagePrice)
        {
            if (!InAnySession(hourlyInfo))
            {
                switch (mode)
                {
                    case Modes.Charging:
                        {
                            Session session = new Session(mode, _maxChargingHours);
                            session.AddHourlyInfo(hourlyInfo);
                            CompleteSession(session, _hourlyInfos, _maxChargingHours, _cycleCost, averagePrice);
                            _sessionList.Add(session);
                            break;
                        }

                    case Modes.Discharging:
                        {
                            Session session = new Session(mode, _maxDischargingHours);
                            session.AddHourlyInfo(hourlyInfo);
                            CompleteSession(session, _hourlyInfos, _maxDischargingHours, _cycleCost, averagePrice);
                            _sessionList.Add(session);
                            break;
                        }

                    default:
                        break;
                }
            }
            else
                _logger.LogInformation($"Overlap in sessions {hourlyInfo}");
        }

        public void CalculateProfits()
        {
            _hourlyInfos
                .Where(hp => hp.ZeroNetHome)
                .ToList()
                .ForEach(hp => hp.Selling = hp.Price * (Math.Min(_homeNeeds / 24, hp.ChargeLeft) / 1000));

            var sessionsList = SessionList.OrderBy(se => se.First).ToList();

            Session? lastSession = null;

            foreach (var session in sessionsList)
            {
                if (lastSession != null)
                {
                    switch (session.Mode)
                    {
                        case Modes.Charging:
                            {
                                session.PriceList.ForEach(hp => hp.Buying = hp.Price * (Math.Min(_totalBatteryCapacity - hp.ChargeLeft, _totalChargingCapacity) / 1000));
                                break;
                            }

                        case Modes.Discharging:
                            {
                                if (lastSession.Mode == Modes.Charging)
                                {
                                    var dischargingPrices = session.PriceList.OrderBy(hp => hp.Price).ToList();
                                    var chargingPrices = lastSession.PriceList.OrderBy(hp => hp.Price).ToList();

                                    var chargingEnum = chargingPrices.GetEnumerator();
                                    var dischargingEnum = dischargingPrices.GetEnumerator();

                                    var hasCharging = chargingEnum.MoveNext();
                                    var hasDischarging = dischargingEnum.MoveNext();

                                    while (hasCharging && hasDischarging)
                                    {
                                        var chargeLeft = Math.Min(_totalDischargingCapacity, dischargingEnum.Current.ChargeLeft);

                                        dischargingEnum.Current.Buying = (chargeLeft / 1000) * chargingEnum.Current.Price;
                                        dischargingEnum.Current.Selling = (chargeLeft / 1000) * dischargingEnum.Current.Price;

                                        hasCharging = chargingEnum.MoveNext();
                                        hasDischarging = dischargingEnum.MoveNext();
                                    }
                                }
                            }
                            break;

                        default:
                            break;
                    }
                }

                lastSession = session;
            }
        }

        public void AddHourlyInfo(Session session, HourlyInfo hourlyInfo)
        {
            if(!InAnySession(hourlyInfo))
            {
                session.AddHourlyInfo(hourlyInfo);
            }
        }

        /// <summary>
        /// Add neighbouring hours to the Session
        /// </summary>
        public void CompleteSession(Session session, List<HourlyInfo> hourlyInfos, int maxHours, double cycleCost, double averagePrice)
        {
            if (session.PriceList.Count != 1)
                throw new InvalidOperationException($"Session has zero or more than 1 hourly price.");

            var index = hourlyInfos.IndexOf(session.PriceList[0]);
            var prev = index - 1;
            var next = index + 1;

            for (var i = 0; i < maxHours - 1; i++)
            {
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
}
