using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;

namespace SessyWeb.Pages
{
    public partial class SessyWebControlPage : PageBase
    {
        [Inject]
        private SessyWebControlDataService? _sessyWebControlDataService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<SessyWebControl>? SessyWebControlList { get; set; }

        RadzenDataGrid<SessyWebControl>? sessyWebControlGrid { get; set; }

        int count { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await sessyWebControlGrid!.FirstPage();
        }

        void LoadData(LoadDataArgs args)
        {
            EnsureEnergyGrid();

            var now = _timeZoneService!.Now;
            var filter = sessyWebControlGrid!.ColumnsCollection;

            SessyWebControlList = _sessyWebControlDataService!.GetList((set) =>
            {
                var query = set
                    .OrderBy(eh => eh.Time)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(args.Filter))
                {
                    query = query.Where(sessyWebControlGrid.ColumnsCollection);
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
            if (sessyWebControlGrid == null) throw new InvalidOperationException($"{nameof(sessyWebControlGrid)} can not be null here, did you forget a @ref?");
        }
    }
}
