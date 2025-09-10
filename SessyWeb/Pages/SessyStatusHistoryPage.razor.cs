using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Services;
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

        public DateTime? DateChosen { get; set; }
        
        public PeriodsEnums PeriodChosen { get; set; }

        private List<GroupedSessyStatus>? StatusHistoryList { get; set; } = new List<GroupedSessyStatus>();

        RadzenDataGrid<GroupedSessyStatus>? historyGrid { get; set; }

        int count { get; set; }


        public async Task DateChosenChanged(DateTime date)
        {
            DateChosen = date;

            await SelectionChanged();
        }

        public async Task PeriodChosenChanged(PeriodsEnums period)
        {
            PeriodChosen = period;

            await SelectionChanged();
        }

        private async Task SelectionChanged()
        {
            await historyGrid.Reload();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            EnsureDataGridRef();

            if (firstRender)
                await historyGrid.FirstPage();
        }

        void LoadData(LoadDataArgs args)
        {
            EnsureDataGridRef();

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

        private void EnsureDataGridRef()
        {
            if (historyGrid == null) throw new InvalidOperationException($"{nameof(historyGrid)} can not be null here, did you forget a @ref?");
        }

        public IQueryable<GroupedSessyStatus> GetGroupedList(ModelContext modelContext)
        {
            var dateChosen = DateChosen!.Value;
            DateTime start;
            DateTime end;

            switch (PeriodChosen)
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
                    throw new InvalidOperationException($"Wrong period {PeriodChosen}");
            }

            return modelContext.SessyStatusHistory
                .Where(sh => sh.Time >= start && sh.Time <= end)    
                .OrderByDescending(x => x.Time)
                .ThenBy(x => x.Name)
                .AsEnumerable() // Stap over naar LINQ-to-Objects
                .GroupBy(x => new { x.Name, x.Status, x.StatusDetails }) // Groeperen op Name, Status, StatusDetails
                .SelectMany(group =>
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
                            if (startTime != null) // Voeg vorige groep toe voordat we resetten
                            {
                                groupedList.Add(new GroupedSessyStatus
                                {
                                    Name = group.Key.Name,
                                    Status = group.Key.Status,
                                    StatusDetails = group.Key.StatusDetails,
                                    StartTime = startTime.Value,
                                    EndTime = endTime!.Value,
                                    Duration = endTime.Value - startTime.Value
                                });
                            }
                            startTime = entry.Time;
                        }

                        // Bijwerken van eindtijd
                        endTime = entry.Time;
                    }

                    // Laatste groep toevoegen
                    if (startTime != null)
                    {
                        groupedList.Add(new GroupedSessyStatus
                        {
                            Name = group.Key.Name,
                            Status = group.Key.Status,
                            StatusDetails = group.Key.StatusDetails,
                            StartTime = startTime.Value,
                            EndTime = endTime!.Value,
                            Duration = endTime.Value - startTime.Value
                        });
                    }

                    return groupedList;
                })
                .AsQueryable();
        }
    }
}

