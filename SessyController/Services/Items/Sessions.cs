using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class Sessions
    {
        public List<Session> List { get; set; }

        public Sessions() 
        {
            List = new List<Session>();
        }

        public void AddSession(Session session)
        {
            List.Add(session);
        }

        public void AddNewSession(Modes mode, HourlyPrice hourlyPrice)
        {
            Session session = new Session(mode);
            session.AddHourlyPrice(hourlyPrice);
            List.Add(session);
        }
    }
}
