using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyData.Services;
using SessyData.Model;
using Microsoft.EntityFrameworkCore;
using Radzen;
using Radzen.Blazor;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class SessyStatusHistoryPage : PageBase
    {
        [Inject]
        private SessyStatusHistoryService? _sessyStatusHistoryService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<SessyStatusHistory>? StatusHistoryList { get; set; }

        RadzenDataGrid<SessyStatusHistory> historyGrid { get; set; }

        int count { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if(firstRender)
                await historyGrid.FirstPage();
        }

        void LoadData(LoadDataArgs args)
        {
            var now = _timeZoneService!.Now;
            var filter = historyGrid.ColumnsCollection;

            StatusHistoryList = _sessyStatusHistoryService!.GetSessyStatusHistory((ModelContext modelContext) =>
            {
                var query = modelContext.SessyStatusHistory.AsQueryable();

                if (!string.IsNullOrEmpty(args.Filter))
                {
                    query = query.Where(historyGrid.ColumnsCollection);
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
