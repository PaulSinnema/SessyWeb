using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class SessyStatusHistoryPage : PageBase
    {
        [Inject]
        private SessyStatusHistoryService? _sessyStatusHistoryService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        [Inject]
        private BatteryContainer? _batteryContainer { get; set; }

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
                BatteryNames = _batteryContainer!.Batteries!
                    .Select(b => b.GetName())
                    .OrderBy(bn => bn)
                    .ToList();

                BatteryNames.Insert(0, "All");

                selectedBattery = "All";

                await historyGrid!.FirstPage();
            }
        }

        void LoadData(LoadDataArgs args)
        {
            IsBusy = true;

            try
            {
                EnsureDataGridRef();

                var now = _timeZoneService!.Now;
                var filter = historyGrid!.ColumnsCollection;

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

                    if (args.Skip > count)
                    {
                        args.Skip = 0;
                        historyGrid.CurrentPage = 0;
                    }

                    return query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
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
            DateTime start;
            DateTime end;

            switch (DateSelectionChosen!.PeriodChosen)
            {
                case PeriodsEnums.Day:
                    start = dateChosen.Date;
                    end = dateChosen.Date.AddDays(1).AddSeconds(-1);
                    break;

                case PeriodsEnums.Week:
                    start = dateChosen.StartOfWeek();
                    end = dateChosen.EndOfWeek(); ;
                    break;

                case PeriodsEnums.Month:
                    start = dateChosen.StartOfMonth();
                    end = start.EndOfMonth();
                    break;

                case PeriodsEnums.Year:
                    start = new DateTime(dateChosen.Year, 1, 1);
                    end = start.AddYears(1).AddDays(-1);
                    break;

                case PeriodsEnums.All:
                    start = DateTime.MinValue;
                    end = DateTime.MaxValue;
                    break;

                default:
                    throw new InvalidOperationException($"Wrong period {DateSelectionChosen!.PeriodChosen}");
            }

            var battery = _batteryContainer!.Batteries!.SingleOrDefault(b => b.GetName() == selectedBattery);

            return modelContext.SessyStatusHistory
                .Where(sh => sh.Time >= start && sh.Time <= end && (battery == null || sh.Name == battery.Id))
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
                                Name = startEntry.Name,
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
                            Name = startEntry.Name,
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

