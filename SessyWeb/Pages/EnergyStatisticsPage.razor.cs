using Microsoft.AspNetCore.Components;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Statistics;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class EnergyStatisticsPage : PageBase
    {
        [Inject]
        private EnergyStatisticsService? _statisticsService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        // Single source of truth — the entire page binds to this object.
        private DashboardStatistics? Dashboard { get; set; }

        protected override async Task OnInitializedAsync()
        {
            await LoadStatisticsAsync(null);
        }

        public bool IsLoading { get; set; } = false;

        private async Task LoadStatisticsAsync(DateArgs? range)
        {
            IsLoading = true;
            IsBusy = true;
            StateHasChanged();

            try
            {
                var start = range?.Start ?? DateTime.MinValue;
                var end = range?.End ?? DateTime.MaxValue;

                Dashboard = await _statisticsService!.GetDashboardStatisticsAsync(start, end);
            }
            finally
            {
                IsLoading = false;
                IsBusy = false;
                StateHasChanged();
            }
        }

        private async Task OnDateRangeChanged(DateArgs range)
        {
            await LoadStatisticsAsync(range);
        }
    }
}