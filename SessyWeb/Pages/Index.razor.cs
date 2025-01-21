using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class Index : PageBase
    {
        public async Task<List<double>> GetFreeCapacity()
        {
            var list = new List<double>();

            foreach (Battery battery in batteryContainer.Batteries)
            {
                list.Add(await battery.GetFreeCapacity().ConfigureAwait(false));
            }

            return list;
        }
    }
}
