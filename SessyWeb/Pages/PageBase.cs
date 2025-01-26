using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public class PageBase : ComponentBase, IDisposable
    {
        [Inject]
        public BatteryContainer? batteryContainer { get; set; }

        public bool IsComponentActive { get; internal set; } = false;

        protected override void OnInitialized()
        {
            IsComponentActive = true;
        }

        public virtual void Dispose()
        {
            IsComponentActive = false;
        }
    }
}

