using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
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

        private List<FinancialMonthResult>? FinancialMonthResultsList { get; set; }

        RadzenDataGrid<FinancialMonthResult>? financialMonthResultsGrid { get; set; }

        int count { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await financialMonthResultsGrid!.FirstPage();
        }

        public double GetMonthlyTotalCost(FinancialMonthResult monthResult)
        {
            return monthResult.FinancialResultsList!.Sum(fr => fr.Cost);
        }
        public void OnRender(DataGridRenderEventArgs<FinancialMonthResult> args)
        {
            if (args.FirstRender)
            {
                args.Grid.Groups.Add(new GroupDescriptor() { Property = nameof(FinancialResult.YearMonth), Title = "Time" });
                StateHasChanged();
            }
        }

        void LoadData(LoadDataArgs args)
        {
            if (financialMonthResultsGrid == null) throw new InvalidOperationException($"{nameof(financialMonthResultsGrid)} can not be null here, did you forget a @ref?");

            var now = _timeZoneService!.Now.DateHour();
            var start = now.AddDays(-(now.Day - 1));
            var end = start.AddDays(30);

            var filter = financialMonthResultsGrid.ColumnsCollection;

            var query = _financialResultsService!.GetFinancialMonthResults(start, end).AsQueryable();

            if (!string.IsNullOrEmpty(args.Filter))
            {
                query = query.Where(financialMonthResultsGrid.ColumnsCollection);
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

