using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;

namespace SessyWeb.Components
{

    public partial class BatteryInfo : ComponentBase
    {
        [Parameter]
        public Battery? Battery { get; set; }
    }
}
