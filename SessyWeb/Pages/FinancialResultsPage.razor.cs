using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Extensions;
using SessyController.Services;
using SessyController.Services.Items;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class FinancialResultsPage : PageBase
    {
        [Inject]
        private FinancialResultsService? _financialResultsService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        [Inject]
        private EnergyMonitorService? _energyMonitorService { get; set; }

        private List<FinancialMonthResult>? FinancialMonthResultsList { get; set; }

        RadzenDataGrid<FinancialMonthResult>? financialResultsGrid { get; set; }

        int count { get; set; }


        public DateTime? DateChosen { get; set; }

        public enum PeriodsEnums
        {
            Day,
            Week,
            Month,
            Year,
            All
        };

        List<PeriodsEnums> Periods = new List<PeriodsEnums>
        {
            PeriodsEnums.Day, PeriodsEnums.Week, PeriodsEnums.Month, PeriodsEnums.Year, PeriodsEnums.All };

        public PeriodsEnums PeriodChosen { get; set; } = PeriodsEnums.Day;

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                DateChosen = _timeZoneService!.Now.Date;
                PeriodChosen = PeriodsEnums.Month;

                await financialResultsGrid!.FirstPage();
            }
        }

        public void DateChanged(DateTime? date)
        {
            SelectionChanged();
        }

        public void PeriodChanged(object obj)
        {
            SelectionChanged();
        }

        private void SelectionChanged()
        {
            financialResultsGrid!.Reload();
        }

        public decimal GetMonthlyTotalCost(FinancialMonthResult monthResult)
        {
            return monthResult.FinancialResultsList!.Sum(fr => fr.Cost);
        }

        public void OnRender(DataGridRenderEventArgs<FinancialMonthResult> args)
        {
            if (args.FirstRender)
            {
                _energyMonitorService!.DataChanged += EnergyMonitorServiceDataChanged;
                args.Grid.Groups.Add(new GroupDescriptor() { Property = nameof(FinancialResult.YearMonth), Title = "Time" });
                StateHasChanged();
            }
        }

        private async Task EnergyMonitorServiceDataChanged()
        {
            await InvokeAsync(async () =>
            {
                await financialResultsGrid!.Reload();
                StateHasChanged();
            });
        }

        void LoadData(LoadDataArgs args)
        {
            if (financialResultsGrid == null) throw new InvalidOperationException($"{nameof(financialResultsGrid)} can not be null here, did you forget a @ref?");

            var chosen = DateChosen!.Value.DateFloorQuarter();
            DateTime start;
            DateTime end;

            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    start = chosen;
                    end = chosen.AddDays(1);
                    break;

                case PeriodsEnums.Week:
                    start = chosen.StartOfWeek();
                    end = chosen.EndOfWeek().AddDays(1);
                    break;

                case PeriodsEnums.Month:
                    start = chosen.StartOfMonth();
                    end = chosen.EndOfMonth().AddDays(1);
                    break;

                case PeriodsEnums.Year:
                    start = new DateTime(chosen.Year, 1, 1);
                    end = new DateTime(chosen.Year, 12, 31);
                    break;

                case PeriodsEnums.All:
                    start = DateTime.MinValue;
                    end = DateTime.MaxValue;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid period {PeriodChosen}");
            }

            var filter = financialResultsGrid.ColumnsCollection;

            var query = _financialResultsService!.GetFinancialMonthResults(start, end).AsQueryable();

            if (!string.IsNullOrEmpty(args.Filter))
            {
                query = query.Where(financialResultsGrid.ColumnsCollection);
            }

            if (!string.IsNullOrEmpty(args.OrderBy))
            {
                query = query.OrderBy(args.OrderBy);
            }

            count = query.Count();

            FinancialMonthResultsList = query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
        }
    }
}

