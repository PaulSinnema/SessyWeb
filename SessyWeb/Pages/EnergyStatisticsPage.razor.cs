using Microsoft.AspNetCore.Components;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Services;
using SessyController.Services.Statistics;

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
            _statisticsService!.ConfigurationChanged += OnConfigurationChanged;

            await LoadStatisticsAsync();
        }

        /// <summary>
        /// Editing appsettings.json changes what the figures mean — a removed HeatPumpConfig turns
        /// its investment into pure cost, which stretches the payback period. Rebuild rather than
        /// leave the previous configuration's numbers on screen.
        /// </summary>
        private void OnConfigurationChanged()
        {
            // Fired from the configuration file watcher's thread, so hop to the circuit first.
            _ = InvokeAsync(async () =>
            {
                if (!IsComponentActive) return;

                await LoadStatisticsAsync();
            });
        }

        public override void Dispose()
        {
            if (_statisticsService != null)
                _statisticsService.ConfigurationChanged -= OnConfigurationChanged;

            base.Dispose();
        }

        private async Task LoadStatisticsAsync()
        {
            IsLoading = true;
            StateHasChanged();

            try
            {
                // Lifetime overview — always the whole history.
                // Task.Run on purpose. Component code runs on the circuit's synchronization
                // context, so without it every continuation inside the statistics service — and it
                // walks the data month by month, with database round trips and LINQ per month —
                // resumes on the dispatcher and blocks every other click for the whole build.
                Dashboard = await Task.Run(() => _statisticsService!.GetDashboardStatisticsAsync(DateTime.MinValue, DateTime.MaxValue))
                    .ConfigureAwait(true);
            }
            finally
            {
                IsLoading = false;
                StateHasChanged();
            }
        }

        private async Task ClearPlanAsync()
        {
            await _milpService!.ClearPlanAsync();
            await LoadStatisticsAsync();
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

            await LoadStatisticsAsync();
        }
    }
}