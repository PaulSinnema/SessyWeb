using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class ChargingHours : PageBase
    {
        [Inject]
        public BatteriesService? BatteriesService { get; set; }

        public List<HourlyPrice>? HourlyPrices { get; set; }

        protected override Task OnInitializedAsync()
        {
            HourlyPrices = BatteriesService?.GetHourlyPrices();

            return base.OnInitializedAsync();
        }
    }
}
