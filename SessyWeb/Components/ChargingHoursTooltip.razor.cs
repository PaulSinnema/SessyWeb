using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyWeb.Pages;

namespace SessyWeb.Components
{
    public partial class ChargingHoursTooltip : BaseComponent
    {
        [Parameter]
        public QuarterlyInfoView? QuarterlyInfo { get; set; }

        [Inject] private SettingsService? _settingsService { get; set; }

        // Cycle cost used in the discharge-profitability check shown in the tooltip.
        protected double CycleCost => _settingsService?.CycleCost ?? 0.0;
    }
}