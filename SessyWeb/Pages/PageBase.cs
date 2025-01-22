using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public class PageBase : ComponentBase
    {
        [Inject]
        public BatteryContainer? batteryContainer { get; set; }
    }
}
