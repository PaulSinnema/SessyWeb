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
        private FinancialResultsService? _finacialResultsService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<FinancialResult>? FinancialResultsList { get; set; }

        RadzenDataGrid<FinancialResult>? financialResultsGrid { get; set; }

        int count { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await financialResultsGrid!.FirstPage();
        }

        void OnRender(DataGridRenderEventArgs<FinancialResult> args)
        {
            if (args.FirstRender)
            {
                args.Grid.Groups.Add(new GroupDescriptor() { Property = nameof(FinancialResult.YearMonth), Title = "Time" });
                StateHasChanged();
            }
        }

        public double GetMonthlyTotalCost(IEnumerable<FinancialResult> items)
        {
            if (items != null)
            {
                var list = items.ToList();

                if (list.Count > 0)
                {
                    var date = list.First().Time;
                    var month = date.Month;
                    var year = date.Year;
                    var day = date.Day;

                    if (FinancialResultsList != null)
                        return FinancialResultsList
                            .Where(fr => fr.Day == day && fr.Month == month && fr.Year == year)
                            .Sum(fr => fr.Cost);
                }
            }

            return 0.0;
        }

        void LoadData(LoadDataArgs args)
        {
            if (financialResultsGrid == null) throw new InvalidOperationException($"{nameof(financialResultsGrid)} can not be null here, did you forget a @ref?");

            var now = _timeZoneService!.Now.DateHour();
            var start = now.AddDays(-(now.Day - 1));
            var end = start.AddDays(30);

            var filter = financialResultsGrid.ColumnsCollection;

            var query = _finacialResultsService!.GetFinancialResults(start, end).AsQueryable();

            if (!string.IsNullOrEmpty(args.Filter))
            {
                query = query.Where(financialResultsGrid.ColumnsCollection);
            }

            if (!string.IsNullOrEmpty(args.OrderBy))
            {
                query = query.OrderBy(args.OrderBy);
            }

            count = query.Count();

            FinancialResultsList = query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
        }
    }
}

