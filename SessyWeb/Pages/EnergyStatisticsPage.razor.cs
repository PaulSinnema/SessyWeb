using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Radzen.Blazor.Rendering;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
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

        private EnergyStatistics? Stats { get; set; }
        private InvestmentStatistics? InvestmentStats { get; set; }
        private HeatPumpStatistics? HeatPumpStats { get; set; }
        private List<MonthlyTrend>? MonthlyTrends { get; set; }
        private List<DailyArbitrageTrend>? DailyArbitrageTrends { get; set; }

        public bool IsLoading { get; set; } = false;

        private DateArgs? DateSelectionChosen { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                // Default to current month.
                DateSelectionChosen = new DateArgs(PeriodsEnums.Month, _timeZoneService!.Now);

                await LoadStatistics();
            }
        }

        public async Task SelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;
            await LoadStatistics();
        }

        private async Task LoadStatistics()
        {
            if (DateSelectionChosen == null)
                return;

            IsBusy = true;
            IsLoading = true;
            StateHasChanged();

            try
            {
                var start = DateTime.MinValue;
                var end = DateTime.MaxValue;

                HeatPumpStats = await _statisticsService!.GetHeatPumpStatisticsAsync();
                Stats = await _statisticsService!.GetEnergyStatisticsAsync(start, end);
                InvestmentStats = await _statisticsService!.GetInvestmentStatisticsAsync();
                DailyArbitrageTrends = await _statisticsService!.GetDailyArbitrageTrendsAsync(start, end);

                // Only load monthly trends for periods longer than a month.
                if (DateSelectionChosen.PeriodChosen == PeriodsEnums.Year ||
                    DateSelectionChosen.PeriodChosen == PeriodsEnums.All)
                {
                    MonthlyTrends = await _statisticsService!.GetMonthlyTrendsAsync(start, end);
                }
                else
                {
                    MonthlyTrends = null;
                }
            }
            finally
            {
                IsLoading = false;
                IsBusy = false;
                StateHasChanged();
            }
        }

        private static (DateTime start, DateTime end) GetPeriodBounds(DateArgs dateArgs)
        {
            var chosen = dateArgs.DateChosen!.Value.DateFloorQuarter();

            return dateArgs.PeriodChosen switch
            {
                PeriodsEnums.Day => (chosen.Date, chosen.Date.AddDays(1).AddSeconds(-1)),
                PeriodsEnums.Week => (chosen.StartOfWeek(), chosen.EndOfWeek().AddDays(1).AddSeconds(-1)),
                PeriodsEnums.Month => (chosen.StartOfMonth(), chosen.EndOfMonth().AddDays(1).AddSeconds(-1)),
                PeriodsEnums.Year => (new DateTime(chosen.Year, 1, 1), new DateTime(chosen.Year, 12, 31, 23, 59, 59)),
                PeriodsEnums.All => (DateTime.MinValue, DateTime.MaxValue),
                _ => throw new InvalidOperationException($"Invalid period {dateArgs.PeriodChosen}")
            };
        }
    }
}