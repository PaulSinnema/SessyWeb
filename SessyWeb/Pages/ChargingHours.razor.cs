using Microsoft.AspNetCore.Components;
using SessyController.Services;

namespace SessyWeb.Pages
{
    public partial class ChargingHours : PageBase
    {
        [Inject]
        public BatteriesService? BatteriesService { get; set; }

        
    }
}
