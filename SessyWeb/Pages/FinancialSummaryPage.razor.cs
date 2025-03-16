using Microsoft.AspNetCore.Components;
using SessyController.Services;

namespace SessyWeb.Pages
{
    public partial class FinancialSummaryPage
    {
        [Inject]
        TimeZoneService? _timezoneService { get; set; }

        DateTime value;

        protected override void OnInitialized()
        {
            value = _timezoneService!.Now;

            base.OnInitialized();
        }

        public void OnCurrentDateChanged(DateTime dateTime)
        {
            value = dateTime;
        }


    }
}
