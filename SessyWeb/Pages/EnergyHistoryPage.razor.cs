using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyController.Services;
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

        void LoadData(LoadDataArgs args)
        {
            if (energyGrid == null) throw new InvalidOperationException($"{nameof(energyGrid)} can not be null here, did you forget a @ref?");

            var now = _timeZoneService!.Now;
            var filter = energyGrid.ColumnsCollection;

            EnergyHistoryList = _energyHistoryService!.GetList((ModelContext modelContext) =>
            {
                var query = modelContext.EnergyHistory
                    .OrderBy(eh => eh.Time)
                    .AsQueryable();

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
    }
}

