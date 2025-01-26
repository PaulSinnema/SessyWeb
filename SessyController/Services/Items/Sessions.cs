using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class Sessions
    {
        private List<Session> _sessionList { get; set; }
        private int _maxChargingHours { get; set; }
        private int _maxDischargingHours { get; set; }
        private double _cycleCost { get; set; }
        private List<HourlyPrice> _hourlyPrices { get; set; }

        public Sessions(List<HourlyPrice> hourlyPrices, int maxChargingHours, int maxDischargingHours, double cycleCost)
        {
            _sessionList = new List<Session>();
            _hourlyPrices = hourlyPrices;
            _maxChargingHours = maxChargingHours;
            _maxDischargingHours = maxDischargingHours;
            _cycleCost = cycleCost;
        }

        public List<Session> SessionList => _sessionList;

        public bool InAnySession(HourlyPrice hourlyPrice)
        {
            foreach (var se in _sessionList)
            {
                if (se.PriceList.Contains(hourlyPrice))
                    return true;
            }

            return false;
        }

        public void AddNewSession(Modes mode, HourlyPrice hourlyPrice, double averagePrice)
        {
            if (!InAnySession(hourlyPrice))
            {
                switch (mode)
                {
                    case Modes.Charging:
                        {
                            Session session = new Session(mode, _maxChargingHours);
                            session.AddHourlyPrice(hourlyPrice);
                            session.CompleteSession(_hourlyPrices, _maxChargingHours, _cycleCost, averagePrice);
                            _sessionList.Add(session);
                            break;
                        }

                    case Modes.Discharging:
                        {
                            Session session = new Session(mode, _maxDischargingHours);
                            session.AddHourlyPrice(hourlyPrice);
                            session.CompleteSession(_hourlyPrices, _maxDischargingHours, _cycleCost, averagePrice);
                            _sessionList.Add(session);
                            break;
                        }

                    default:
                        break;
                }
            }
            else
                Console.WriteLine($"Overlap in sessions {hourlyPrice}");
        }

        public void OptimizeChargingSessions()
        {

        }
    }
}
