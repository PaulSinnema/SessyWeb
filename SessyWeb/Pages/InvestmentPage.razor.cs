using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class InvestmentPage : PageBase
    {
        [Inject]
        TimeZoneService? _timezoneService { get; set; }

        [Inject]
        private InvestmentDataService? _investmentService { get; set; }

        private List<Investment>? InvestmentList { get; set; } = new();

        RadzenDataGrid<Investment>? investmentsGrid { get; set; }

        public bool isLoading { get; set; } = false;

        int count { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await investmentsGrid!.FirstPage(true);
        }

        async Task LoadData(LoadDataArgs args)
        {
            IsBusy = true;

            try
            {
                isLoading = true;

                await Task.Yield();

                EnsureInvestmentsGrid();

                InvestmentList = await _investmentService!.GetList(async (set) =>
                {
                    var result = set
                        .OrderBy(i => i.PurchaseDate)
                        .AsQueryable();

                    var query = await Task.FromResult(result);

                    if (!string.IsNullOrEmpty(args.Filter))
                    {
                        query = query.Where(investmentsGrid!.ColumnsCollection);
                    }

                    if (!string.IsNullOrEmpty(args.OrderBy))
                    {
                        query = query.OrderBy(args.OrderBy);
                    }

                    count = query.Count();

                    return query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
                });

                isLoading = false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void EnsureInvestmentsGrid()
        {
            if (investmentsGrid == null)
                throw new InvalidOperationException($"{nameof(investmentsGrid)} can not be null here, did you forget a @ref?");
        }

        async Task EditRow(Investment investment)
        {
            if (!investmentsGrid!.IsValid) return;

            await investmentsGrid!.EditRow(investment);
        }

        async Task OnUpdateRow(Investment investment)
        {
            await _investmentService!.Update(
                new List<Investment> { investment },
                (item, set) => set.Where(i => i.Id == investment.Id).FirstOrDefault());
        }

        async Task SaveRow(Investment investment)
        {
            await investmentsGrid!.UpdateRow(investment);
        }

        async Task CancelEdit(Investment investment)
        {
            investmentsGrid!.CancelEditRow(investment);

            await investmentsGrid.Reload();
        }

        async Task DeleteRow(Investment investment)
        {
            if (investment.Id != 0)
                await _investmentService!.Remove(
                    new List<Investment> { investment },
                    (item, set) => set.Where(i => i.Id == item.Id).FirstOrDefault());

            await investmentsGrid!.Reload();
        }

        async Task InsertRow()
        {
            if (!investmentsGrid!.IsValid) return;

            var investment = new Investment
            {
                PurchaseDate = _timezoneService!.Now.Date,
                ExpectedLifetimeYears = 25
            };

            await investmentsGrid.InsertRow(investment);
            count++;
        }

        async Task InsertAfterRow(Investment row)
        {
            if (!investmentsGrid!.IsValid) return;

            var investment = new Investment
            {
                PurchaseDate = _timezoneService!.Now.Date,
                ExpectedLifetimeYears = 25
            };

            await investmentsGrid.InsertAfterRow(investment, row);
            count++;
        }

        private async Task OnCreateRow(Investment investment)
        {
            await _investmentService!.Add(
                        new List<Investment> { investment },
                        (item, set) => set.Contains(investment));
        }
    }
}
