using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class Index : PageBase
    {
        public List<Battery> GetBatteries()
        {
            var list = new List<Battery>();

            foreach (Battery battery in batteryContainer.Batteries)
            {
                list.Add(battery);
            }

            return list;
        }
    }
}
