using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Per month how much of the consumption came from own solar and battery instead of the grid.</summary>
    public partial class SelfSufficiencyTrendComponent : BaseComponent
    {
        [Inject]
        private EnergyStatisticsService? EnergyStatisticsService { get; set; }

        /// <summary>Flat row for Radzen, which cannot bind to the computed month label of MonthlyTrend.</summary>
        public sealed record Row(string Label, double ConsumptionKWh, double GridImportKWh, double SelfSufficiencyPct, double SelfConsumptionPct);

        private const string HowToRead =
            "Bars: what the house used and what came from the grid, per month. Self-sufficiency: the share of the " +
            "consumption covered by your own solar panels and battery. Solar used at home: the share of the solar " +
            "production used in the house or stored in the battery instead of exported. Summer months are naturally " +
            "high, winter months low; compare a month with the same month a year earlier.";

        private bool _hasSolar;
        private List<Row> _months = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (EnergyStatisticsService == null) return;

            await WhileLoadingAsync(async () =>
            {
                // MinValue: from the first stored data; one statistics pass per month, so off the circuit thread.
                var trends = await Task.Run(() => EnergyStatisticsService.GetMonthlyTrendsAsync(DateTime.MinValue, DateTime.MaxValue)).ConfigureAwait(true);

                _months = trends
                    .Where(t => t.ConsumptionKWh > 0.0)
                    .Select(t => new Row(t.PeriodStart.ToString("MMM yyyy"), t.ConsumptionKWh, t.GridImportKWh, t.SelfSufficiencyPct, t.SelfConsumptionPct))
                    .ToList();

                _hasSolar = trends.Any(t => t.SolarProductionKWh > 0.0);
            });
        }
    }
}
