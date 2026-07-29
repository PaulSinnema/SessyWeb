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

        [Inject]
        private LoggingService<EnergyStatisticsPage>? _logger { get; set; }

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

        /// <summary>
        /// Solves again immediately and keeps the result. A forced rebuild skips the
        /// EUR/quarter comparison, so the new plan also replaces a currently better-scoring one.
        /// </summary>
        private async Task ForceNewPlanAsync()
        {
            IsLoading = true;
            StateHasChanged();

            try
            {
                _milpService!.ForceRebuild("Manual rebuild from Statistics page");

                // Run a cycle now instead of waiting up to a minute for the next one.
                // Process() takes the BatteriesService semaphore itself.
                await _batteriesService!.Process(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Forcing a new plan failed: {ex.Message}");
            }

            await LoadStatisticsAsync(null);
        }
    }
}