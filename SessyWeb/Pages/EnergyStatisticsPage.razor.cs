using Microsoft.AspNetCore.Components;
using SessyCommon.Services;
using SessyController.Interfaces;
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

        [Inject]
        private IMilpService? _milpService { get; set; }

        // Single source of truth — the entire page binds to this object.
        private DashboardStatistics? Dashboard { get; set; }

        private bool IsLoading { get; set; } = false;

        protected override async Task OnInitializedAsync()
        {
            await LoadStatisticsAsync(null);
        }

        private async Task LoadStatisticsAsync(DateArgs? args)
        {
            IsLoading = true;
            StateHasChanged();

            try
            {
                var start = args?.Start ?? DateTime.MinValue;
                var end = args?.End ?? DateTime.MaxValue;

                Dashboard = await _statisticsService!.GetDashboardStatisticsAsync(start, end);
            }
            finally
            {
                IsLoading = false;
                StateHasChanged();
            }
        }

        private async Task OnDateRangeChanged(DateArgs args)
        {
            await LoadStatisticsAsync(args);
        }

        private async Task ClearPlanAsync()
        {
            await _milpService!.ClearPlanAsync();
            await LoadStatisticsAsync(null);
        }
    }
}