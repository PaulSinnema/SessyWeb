using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class SessyStatusHistoryPage : PageBase
    {
        [Inject]
        private SessyStatusHistoryService? _sessyStatusHistoryService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<GroupedSessyStatus>? StatusHistoryList { get; set; }

        RadzenDataGrid<GroupedSessyStatus> historyGrid { get; set; }

        int count { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await historyGrid.FirstPage();
        }

        void LoadData(LoadDataArgs args)
        {
            var now = _timeZoneService!.Now;
            var filter = historyGrid.ColumnsCollection;

            StatusHistoryList = _sessyStatusHistoryService!.GetSessyStatusHistory((ModelContext modelContext) =>
            {
                var query = GetGroupedList(modelContext);

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

        private IQueryable<GroupedSessyStatus> GetGroupedList(ModelContext modelContext)
        {
            return modelContext.SessyStatusHistory
             .OrderBy(x => x.Name)
             .ThenBy(x => x.Time)
             .AsEnumerable() // Stap over naar LINQ-to-Objects
             .GroupBy(x => new { x.Name, x.Status, x.StatusDetails }, (key, group) =>
             {
                 var sortedGroup = group.OrderBy(x => x.Time).ToList();
                 var groupedList = new List<GroupedSessyStatus>();

                 DateTime? startTime = null;
                 DateTime? endTime = null;

                 foreach (var entry in sortedGroup)
                 {
                     if (startTime == null || (entry.Time - endTime)?.TotalMinutes >= 2)
                     {
                         // Nieuwe groep starten
                         startTime = entry.Time;
                     }

                     // Bijwerken van eindtijd
                     endTime = entry.Time;

                     groupedList.Add(new GroupedSessyStatus
                     {
                         Name = key.Name,
                         Status = key.Status,
                         StatusDetails = key.StatusDetails,
                         StartTime = startTime.Value,
                         EndTime = endTime.Value,
                         Duration = endTime.Value - startTime.Value
                     });
                 }

                 return groupedList.Last(); // Alleen de laatste uit de groep teruggeven
             })
             .AsQueryable();

        }
    }
}
