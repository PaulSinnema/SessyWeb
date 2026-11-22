using Microsoft.AspNetCore.Components;
using SessyCommon.Extensions;
using Radzen;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;
using static SessyWeb.Components.DateChooserComponent;
using SessyWeb.Helpers;

namespace SessyWeb.Pages
{
    public partial class SessyStatusHistoryPage : PageBase
    {
        [Inject]
        private SessyStatusHistoryService? _sessyStatusHistoryService { get; set; }

        public DateArgs? DateSelectionChosen { get; set; }

        private List<GroupedSessyStatus>? StatusHistoryList { get; set; } = new List<GroupedSessyStatus>();

        RadzenDataGrid<GroupedSessyStatus>? historyGrid { get; set; }

        public List<string?> BatteryNames { get; set; } = new List<string?>();

        public string selectedBattery { get; set; } = string.Empty;

        int count { get; set; }

        public async Task DateSelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;

            await SelectionChanged();
        }

        public async Task BatteryChanged()
        {
            await SelectionChanged();
        }

        private async Task SelectionChanged()
        {
            await historyGrid!.Reload();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            EnsureDataGridRef();

            if (firstRender)
            {
                BatteryNames = batteryContainer!.Batteries!
                    .Select(b => b.GetName())
                    .OrderBy(bn => bn)
                    .ToList();

                BatteryNames.Insert(0, "All");

                selectedBattery = "All";

                await historyGrid!.FirstPage();
            }
        }

        async Task LoadData(LoadDataArgs args)
        {
            IsBusy = true;

            try
            {
                EnsureDataGridRef();

                var now = _timeZoneService!.Now;
                var filter = historyGrid!.ColumnsCollection;

                StatusHistoryList = await _sessyStatusHistoryService!.GetSessyStatusHistory((ModelContext modelContext) =>
                {
                    var query = GetGroupedList(modelContext);

                    var (page, total) = query.ToGridPage(args, historyGrid);
                    count = total;

                    return page;
                });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void EnsureDataGridRef()
        {
            if (historyGrid == null) throw new InvalidOperationException($"{nameof(historyGrid)} can not be null here, did you forget a @ref?");
        }

        public IQueryable<GroupedSessyStatus> GetGroupedList(ModelContext modelContext)
        {
            var dateChosen = DateSelectionChosen!.DateChosen!.Value;
            var (start, end) = PeriodRange.For(DateSelectionChosen.PeriodChosen, dateChosen);

            var battery = batteryContainer!.Batteries!.SingleOrDefault(b => b.GetName() == selectedBattery);

            return modelContext.SessyStatusHistory
                .Where(sh => sh.Time >= start && sh.Time < end && (battery == null || sh.Name == battery.Id))
                .OrderByDescending(x => x.Time)
                .ThenBy(x => x.Name)
                .AsEnumerable() // Stap over naar LINQ-to-Objects
                .GroupBy(x => new { x.Name }) // Groeperen op Name, Status, StatusDetails
                .SelectMany(group =>
                {
                    var sortedGroup = group.OrderByDescending(x => x.Time).ToList();
                    var groupedList = new List<GroupedSessyStatus>();

                    SessyStatusHistory? startEntry = null;
                    SessyStatusHistory? endEntry = null;

                    foreach (var entry in sortedGroup)
                    {
                        endEntry = entry;

                        // Nieuwe groep starten
                        if (startEntry != null) // Voeg vorige groep toe voordat we resetten
                        {
                            groupedList.Add(new GroupedSessyStatus
                            {
                                Name = batteryContainer.GetName(startEntry.Name!),
                                Status = startEntry.Status,
                                StatusDetails = startEntry.StatusDetails,
                                StartTime = endEntry.Time,
                                EndTime = startEntry.Time,
                                Duration = endEntry.Time - startEntry.Time
                            });
                        }

                        startEntry = entry;
                    }

                    // Laatste groep toevoegen
                    if (startEntry != null)
                    {
                        groupedList.Add(new GroupedSessyStatus
                        {
                            Name = batteryContainer.GetName(startEntry.Name!),
                            Status = startEntry.Status,
                            StatusDetails = startEntry.StatusDetails,
                            StartTime = startEntry.Time,
                            EndTime = endEntry!.Time,
                            Duration = endEntry.Time - startEntry.Time
                        });
                    }

                    return groupedList;
                })
                .AsQueryable();
        }
    }
}

