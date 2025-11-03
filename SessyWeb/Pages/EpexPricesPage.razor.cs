using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class EpexPricesPage : PageBase
    {
        [Inject]
        private EPEXPricesDataService? _epexPricesDataService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<EPEXPrices>? EPEXPricesList { get; set; }

        RadzenDataGrid<EPEXPrices>? epexPricesGrid { get; set; }

        private int Count { get; set; }

        private DateArgs? DateSelectionChosen { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await epexPricesGrid!.FirstPage();
        }

        async Task LoadData(LoadDataArgs args)
        {
            IsBusy = true;

            try
            {
                EnsureEnergyGrid();

                if (DateSelectionChosen != null)
                {
                    var now = _timeZoneService!.Now;

                    var filter = epexPricesGrid!.ColumnsCollection;

                    EPEXPricesList = await _epexPricesDataService!.GetList(async (set) =>
                    {
                        var result = set
                            .Where(eh => eh.Time >= DateSelectionChosen!.Start && eh.Time < DateSelectionChosen.End)
                            .OrderBy(eh => eh.Time)
                            .AsQueryable();

                        var query = await Task.FromResult(result);

                        if (!string.IsNullOrEmpty(args.Filter))
                        {
                            query = query.Where(epexPricesGrid.ColumnsCollection);
                        }

                        if (!string.IsNullOrEmpty(args.OrderBy))
                        {
                            query = query.OrderBy(args.OrderBy);
                        }

                        Count = query.Count();

                        if (args.Skip > Count)
                        {
                            args.Skip = 0;
                            epexPricesGrid.CurrentPage = 0;
                        }

                        return query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
                    });
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void EnsureEnergyGrid()
        {
            if (epexPricesGrid == null) throw new InvalidOperationException($"{nameof(epexPricesGrid)} can not be null here, did you forget a @ref?");
        }

        public async Task SelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;

            await epexPricesGrid!.Reload();
        }

        async Task EditRow(EPEXPrices history)
        {
            if (!epexPricesGrid!.IsValid) return;

            await epexPricesGrid!.EditRow(history);
        }

        private async Task OnUpdateRow(EPEXPrices EPEXPrices)
        {
            List<EPEXPrices> list = new List<EPEXPrices> { EPEXPrices };

            await _epexPricesDataService!.Update(list, (item, set) => set.Where(eh => eh.Id == EPEXPrices.Id).FirstOrDefault());
        }

        async Task SaveRow(EPEXPrices EPEXPrices)
        {
            List<EPEXPrices> list = new List<EPEXPrices> { EPEXPrices };

            await _epexPricesDataService!.AddOrUpdate(list, (item, set) => set.Where(eh => eh.Id == EPEXPrices.Id).FirstOrDefault());

            await epexPricesGrid!.UpdateRow(EPEXPrices);
        }

        async Task CancelEdit(EPEXPrices EPEXPrices)
        {
            epexPricesGrid!.CancelEditRow(EPEXPrices);

            await epexPricesGrid.Reload();
        }

        async Task DeleteRow(EPEXPrices EPEXPrices)
        {
            await _epexPricesDataService!.Remove(new List<EPEXPrices> { EPEXPrices }, (item, set) => set.Where(eh => eh.Id == item.Id).FirstOrDefault());

            await epexPricesGrid!.Reload();
        }

        async Task InsertRow()
        {
            if (!epexPricesGrid!.IsValid) return;

            var EPEXPrices = new EPEXPrices();
            await epexPricesGrid.InsertRow(EPEXPrices);
        }

        async Task InsertAfterRow(EPEXPrices row)
        {
            if (!epexPricesGrid!.IsValid) return;

            var EPEXPrices = new EPEXPrices();
            await epexPricesGrid.InsertAfterRow(EPEXPrices, row);
        }

        private async Task OnCreateRow(EPEXPrices epexPrices)
        {
            await _epexPricesDataService!.Add(new List<EPEXPrices> { epexPrices }, (item, set) => set.Where(eh => eh.Id == item.Id).FirstOrDefault());
        }
    }
}
