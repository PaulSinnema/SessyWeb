using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Extensions;
using SessyController.Services;
using SessyController.Services.Items;
using System.Linq.Dynamic.Core;
using static SessyWeb.Components.DateChooserComponent;

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

        int Count { get; set; }

        public decimal TotalCost { get; set; } = 0;

        public DateTime? DateChosen { get; set; }

        public bool ExpandAllGroups { get; set; } = true;

        public PeriodsEnums PeriodChosen { get; set; } = PeriodsEnums.Day;


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                DateChosen = _timeZoneService!.Now.Date;

                await financialResultsGrid!.FirstPage();
            }
        }

        public async Task PeriodChosenChanged(PeriodsEnums period)
        {
            PeriodChosen = period;

            await financialResultsGrid!.Reload();
        }

        public async Task DateChosenChanged(DateTime date)
        {
            DateChosen = date;

            await financialResultsGrid!.Reload();
        }

        public decimal GetMonthlyTotalCost(FinancialMonthResult monthResult)
        {
            return monthResult.FinancialResultsList!.Sum(fr => fr.Cost);
        }

        public decimal GetTotalCost(List<FinancialMonthResult> list)
        {
            if(list != null)
                return list.Sum(fr => fr.TotalCost);

            return 0;
        }

        public void OnRender(DataGridRenderEventArgs<FinancialMonthResult> args)
        {
            if (args.FirstRender)
            {
                _energyMonitorService!.DataChanged += EnergyMonitorServiceDataChanged;
                args.Grid.Groups.Add(new GroupDescriptor() { Property = nameof(FinancialResult.YearMonth), Title = "Time" });
            }
        }

        private async Task EnergyMonitorServiceDataChanged()
        {
            await InvokeAsync(async () =>
            {
                await financialResultsGrid!.Reload();
            });
        }

        async Task LoadData(LoadDataArgs args)
        {
            if (financialResultsGrid == null) throw new InvalidOperationException($"{nameof(financialResultsGrid)} can not be null here, did you forget a @ref?");

            var chosen = DateChosen!.Value.DateFloorQuarter();
            DateTime start;
            DateTime end;

            ExpandAllGroups = true;

            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    start = chosen.Date;
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
                    ExpandAllGroups = false;
                    break;

                case PeriodsEnums.All:
                    start = DateTime.MinValue;
                    end = DateTime.MaxValue;
                    ExpandAllGroups = false;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid period {PeriodChosen}");
            }

            var filter = financialResultsGrid.ColumnsCollection;

            var query = await _financialResultsService!.GetFinancialMonthResults(start, end);

            if (!string.IsNullOrEmpty(args.Filter))
            {
                query = query.Where(financialResultsGrid.ColumnsCollection);
            }

            if (!string.IsNullOrEmpty(args.OrderBy))
            {
                query = query.OrderBy(args.OrderBy);
            }

            TotalCost = query.Sum(fr => fr.TotalCost);
            Count = query.Count();

            FinancialMonthResultsList = query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();

            StateHasChanged();
        }
    }
}

