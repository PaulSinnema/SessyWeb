using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyData.Services;
using SessyData.Model;

namespace SessyWeb.Pages
{
    public partial class SessyStatusHistoryPage : PageBase
    {
        [Inject]
        private SessyStatusHistoryService? _sessyStatusHistoryService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<SessyStatusHistory>? StatusHistoryList { get; set; }

        protected override void OnInitialized()
        {
            base.OnInitialized();

            var now = _timeZoneService!.Now.AddDays(-30);

            StatusHistoryList = _sessyStatusHistoryService!.GetSessyStatusHistory(now, 40);
        }
    }
}
