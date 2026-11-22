using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Helpers;
using System.Linq.Dynamic.Core;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class FinancialResultsPage : PageBase
    {
        [Inject]
        private FinancialResultsService? _financialResultsService { get; set; }

        [Inject]
        private EnergyMonitorService? _energyMonitorService { get; set; }

        private List<FinancialMonthResult>? FinancialMonthResultsList { get; set; }

        RadzenDataGrid<FinancialMonthResult>? financialResultsGrid { get; set; }

        int Count { get; set; }

        public decimal TotalCost { get; set; } = 0;

        // Grid import over the selected period (kWh).
        public double TotalConsumedKWh { get; set; } = 0;


        public bool ExpandAllGroups { get; set; } = true;

        public DateArgs? DateSelectionChosen { get; private set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await financialResultsGrid!.FirstPage();
            }
        }

        public async Task SelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;

            await financialResultsGrid!.Reload();
        }

        public decimal GetMonthlyTotalCost(FinancialMonthResult monthResult)
        {
            return monthResult.FinancialResultsList!.Sum(fr => fr.Cost);
        }

        public double GetMonthlyConsumedKWh(FinancialMonthResult monthResult)
        {
            return monthResult.FinancialResultsList!.Sum(fr => fr.Consumed) / 1000.0;
        }

        public void OnRender(DataGridRenderEventArgs<FinancialMonthResult> args)
        {
            if (args.FirstRender)
            {
                _energyMonitorService!.DataChanged += EnergyMonitorServiceDataChanged;
                args.Grid!.Groups.Add(new GroupDescriptor() { Property = nameof(FinancialResult.YearMonth), Title = "Time" });
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
            IsBusy = true;

            try
            {
                if (financialResultsGrid == null) throw new InvalidOperationException($"{nameof(financialResultsGrid)} can not be null here, did you forget a @ref?");

                var chosen = DateSelectionChosen!.DateChosen!.Value.DateFloorQuarter();
                var (start, end) = PeriodRange.For(DateSelectionChosen.PeriodChosen, chosen);

                ExpandAllGroups = DateSelectionChosen.PeriodChosen is not (PeriodsEnums.Year or PeriodsEnums.All);

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
                TotalConsumedKWh = query.ToList().Sum(GetMonthlyConsumedKWh);
                Count = query.Count();

                if (args.Skip > Count)
                {
                    args.Skip = 0;
                    financialResultsGrid.CurrentPage = 0;
                }

                FinancialMonthResultsList = query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
