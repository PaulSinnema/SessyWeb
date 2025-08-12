using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class EnergyHistoryPage : PageBase
    {
        [Inject]
        private EnergyHistoryService? _energyHistoryService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<EnergyHistory>? EnergyHistoryList { get; set; }

        RadzenDataGrid<EnergyHistory>? energyGrid { get; set; }

        int count { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await energyGrid!.FirstPage();
        }

        async Task LoadData(LoadDataArgs args)
        {
            EnsureEnergyGrid();

            var now = _timeZoneService!.Now;
            var filter = energyGrid!.ColumnsCollection;

            EnergyHistoryList = await _energyHistoryService!.GetList(async (set) =>
            {
                var result = set
                    .OrderBy(eh => eh.Time)
                    .AsQueryable();

                var query = await Task.FromResult(result);

                if (!string.IsNullOrEmpty(args.Filter))
                {
                    query = query.Where(energyGrid.ColumnsCollection);
                }

                if (!string.IsNullOrEmpty(args.OrderBy))
                {
                    query = query.OrderBy(args.OrderBy);
                }

                count = query.Count();

                return query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
            });
        }

        private void EnsureEnergyGrid()
        {
            if (energyGrid == null) throw new InvalidOperationException($"{nameof(energyGrid)} can not be null here, did you forget a @ref?");
        }


        async Task EditRow(EnergyHistory history)
        {
            if (!energyGrid!.IsValid) return;

            await energyGrid!.EditRow(history);
        }

        void OnUpdateRow(EnergyHistory energyHistory)
        {
            List<EnergyHistory> list = new List<EnergyHistory> { energyHistory };

            _energyHistoryService!.Update(list, (item, set) => set.Where(eh => eh.Id == energyHistory.Id).FirstOrDefault());
        }

        async Task SaveRow(EnergyHistory energyHistory)
        {
            List<EnergyHistory> list = new List<EnergyHistory> { energyHistory };

            await _energyHistoryService!.AddOrUpdate(list, (item, set) => set.Where(eh => eh.Id == energyHistory.Id).FirstOrDefault());

            await energyGrid!.UpdateRow(energyHistory);
        }

        async Task CancelEdit(EnergyHistory energyHistory)
        {
            energyGrid!.CancelEditRow(energyHistory);

            await energyGrid.Reload();
        }

        async Task DeleteRow(EnergyHistory energyHistory)
        {
            await _energyHistoryService!.Remove(new List<EnergyHistory> { energyHistory }, (item, set) => set.Where(eh => eh.Id == item.Id).FirstOrDefault());

            await energyGrid!.Reload();
        }

        async Task InsertRow()
        {
            if (!energyGrid!.IsValid) return;

            var energyHistory = new EnergyHistory();
            await energyGrid.InsertRow(energyHistory);
        }

        async Task InsertAfterRow(EnergyHistory row)
        {
            if (!energyGrid!.IsValid) return;

            var energyHistory = new EnergyHistory();
            await energyGrid.InsertAfterRow(energyHistory, row);
        }

        void OnCreateRow(EnergyHistory energyHistory)
        {
            _energyHistoryService!.Add(new List<EnergyHistory> { energyHistory }, (item, set) => set.Where(eh => eh.Id == item.Id).FirstOrDefault());
        }
    }
}