using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services;
using SessyData.Services;
using SessyWeb.Helpers;
using System.Globalization;
using ConsumptionRow = SessyData.Model.Consumption;
using Unfucked.DateTime;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class ConsumptionPage : PageBase
    {
        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }
        [Inject]
        private ConsumptionDataService? _consumptionDataService { get; set; }
        [Inject]
        private ConsumptionMonitorService? _consumptionMonitorService { get; set; }

        private DateArgs? DateSelectionChosen { get; set; }

        private RadzenChart? ConsumptionDayChart { get; set; }
        private RadzenChart? ConsumptionWeekChart { get; set; }
        private RadzenChart? ConsumptionMonthChart { get; set; }
        private RadzenChart? ConsumptionYearChart { get; set; }
        private RadzenChart? ConsumptionAllChart { get; set; }
        private RadzenChart? ConsumptionCustomChart { get; set; }

        private string GraphStyle { get; set; } = "width: 100%; height: 60vh";

        // The chart gets a fixed pixel width per period; the card grows with it instead of clipping.
        private const string CardStyle = "width: fit-content; min-width: 100%;";

        private int TickDistance { get; set; }

        public List<ConsumptionDisplayDayData> ConsumptionDayData { get; set; } = new();
        public List<ConsumptionDisplayWeekData> ConsumptionWeekData { get; set; } = new();
        public List<ConsumptionDisplayMonthData> ConsumptionMonthData { get; set; } = new();
        public List<ConsumptionDisplayYearData> ConsumptionYearData { get; set; } = new();
        public List<ConsumptionDisplayAllData> ConsumptionAllData { get; set; } = new();
        // Month bars padded to all days of the month; ConsumptionMonthData holds only days with data.
        public List<ConsumptionDisplayMonthData> ConsumptionMonthBars { get; set; } = new();

        // Comparison with earlier years, keyed by offset in years (0 = chosen period).
        public Dictionary<int, List<ConsumptionDisplayDayData>> DayCompare { get; set; } = new();
        public Dictionary<int, List<ConsumptionDisplayWeekData>> WeekPerYear { get; set; } = new();
        public Dictionary<int, List<ConsumptionDisplayMonthData>> MonthCompare { get; set; } = new();
        public Dictionary<int, List<ConsumptionDisplayYearData>> YearPerYear { get; set; } = new();
        public Dictionary<int, List<ConsumptionDisplayCustomData>> CustomCompare { get; set; } = new();
        // Week/Year/All items with measurements only, for the per-year temperature lines.
        public Dictionary<int, List<ConsumptionDisplayWeekData>> WeekWithData { get; set; } = new();
        public Dictionary<int, List<ConsumptionDisplayYearData>> YearWithData { get; set; } = new();
        public Dictionary<int, List<ConsumptionDisplayAllData>> AllWithData { get; set; } = new();
        public Dictionary<int, string> CompareTitles { get; set; } = new();
        // Offsets shown, oldest first.
        public List<int> ShownOffsets { get; set; } = new();

        public enum CompareModes { None, PreviousYear, AllYears }
        public record CompareOption(CompareModes Mode, string Text);
        private static readonly List<CompareOption> CompareOptions =
        [
            new(CompareModes.None, "None"),
            new(CompareModes.PreviousYear, "Previous year"),
            new(CompareModes.AllYears, "All years")
        ];
        private CompareModes CompareMode { get; set; } = CompareModes.PreviousYear;
        private bool ShowWeather { get; set; } = true;

        private DateTime? _earliestTime;
        // Comparable totals: one year back and the matching part of the chosen period; null = not available.
        private double? _previousYearKWh;
        private double _comparedCurrentKWh;
        // Set when only part of the period could be compared (data starts later).
        private DateTime? _comparedFrom;
        public string DeltaPreviousYearText => _previousYearKWh is > 0
            ? $"{(_comparedCurrentKWh - _previousYearKWh.Value) / _previousYearKWh.Value * 100:+0.0;-0.0;0.0} %"
              + (_comparedFrom.HasValue ? $" (from {_comparedFrom:dd/MM/yyyy})" : string.Empty)
            : "n/a";
        public List<int> AllYears { get; set; } = new();
        // Per year all 12 months (missing months zero) so the years line up side by side.
        public Dictionary<int, List<ConsumptionDisplayAllData>> ConsumptionAllPerYear { get; set; } = new();
        // Weather per calendar month, averaged over all years.
        public List<ConsumptionDisplayAllData> ConsumptionAllWeather { get; set; } = new();
        public List<ConsumptionDisplayCustomData> ConsumptionCustomData { get; set; } = new();

        private double _totalConsumptionKWh  {  get; set; } = 0;
        public double TotalConsumptionKWh  => _totalConsumptionKWh  / 1000;

        // Average temperature over the currently shown period, for the footer.
        public double AverageTemperature => DateSelectionChosen?.PeriodChosen switch
        {
            PeriodsEnums.Day => ConsumptionDayData.Count > 0 ? ConsumptionDayData.Average(cd => cd.Temperature) : 0,
            PeriodsEnums.Week => ConsumptionWeekData.Count > 0 ? ConsumptionWeekData.Average(cd => cd.Temperature) : 0,
            PeriodsEnums.Month => ConsumptionMonthData.Count > 0 ? ConsumptionMonthData.Average(cd => cd.Temperature) : 0,
            PeriodsEnums.Year => ConsumptionYearData.Count > 0 ? ConsumptionYearData.Average(cd => cd.Temperature) : 0,
            PeriodsEnums.All => ConsumptionAllData.Count > 0 ? ConsumptionAllData.Average(cd => cd.Temperature) : 0,
            PeriodsEnums.Custom => ConsumptionCustomData.Count > 0 ? ConsumptionCustomData.Average(cd => cd.Temperature) : 0,
            _ => 0
        };

        protected override void OnParametersSet()
        {
            DateSelectionChosen ??= new DateArgs(PeriodsEnums.Day, DurationEnums.Last7Days, _timeZoneService!.Now);

            base.OnParametersSet();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                DateSelectionChosen = new DateArgs(PeriodsEnums.Day, DurationEnums.Last7Days, _timeZoneService!.Now);

                _consumptionMonitorService!.DataChanged += SelectionChanged;
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        /// <summary>
        /// Handle the height of the screen.
        /// </summary>
        public override void ScreenInfoChanged(ScreenInfo screenInfo)
        {
            ConsumptionChartWidth = ScreenInfo!.Width == 0 ? 2300 : ScreenInfo!.Width;

            HandleScreenHeight();
        }

        private void HandleScreenHeight()
        {
            var height = ScreenInfo!.Height;
            var width = ScreenInfo!.Width;

            HandleResize(height, width);
        }

        /// <summary>
        /// The window is resized. Handle it.
        /// </summary>
        private async void HandleResize(int height, int width)
        {
            await InvokeAsync(() =>
            {
                ChangeChartStyle(height);
            });
        }

        /// <summary>
        /// Change the width of the chart depending on the number of quarterlyInfo objects.
        /// </summary>
        private void ChangeChartStyle(int height)
        {
            if (DateSelectionChosen != null)
            {// 25 pixels per data row
                int? width = 1000; // null = fill the card

                switch (DateSelectionChosen!.PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        width = 96 * 35;
                        break;

                    case PeriodsEnums.Week:
                        width = null;
                        break;

                    case PeriodsEnums.Month:
                        // Fixed bar width based on days in month, not days loaded yet, so a partial month doesn't look squeezed.
                        var dateChoosen = DateSelectionChosen.DateChosen?.Date ?? _timeZoneService!.Now.Date;
                        width = DateTime.DaysInMonth(dateChoosen.Year, dateChoosen.Month) * 60;
                        break;

                    case PeriodsEnums.Year:
                        width = null;
                        break;

                    case PeriodsEnums.All:
                        width = null;
                        break;

                    case PeriodsEnums.Custom:
                        switch (DateSelectionChosen.DurationChosen)
                        {
                            case DurationEnums.Last7Days:
                                width = ConsumptionCustomData.Count * 280;
                                break;
                            case DurationEnums.Last30Days:
                                width = ConsumptionCustomData.Count * 70;
                                break;
                            case DurationEnums.Last90Days:
                                width = ConsumptionCustomData.Count * 560;
                                break;
                            case DurationEnums.Last180Days:
                                width = ConsumptionCustomData.Count * 280;
                                break;
                            case DurationEnums.Last365Days:
                                width = ConsumptionCustomData.Count * 180;
                                break;
                            default:
                                break;
                        }

                        break;

                    default:
                        throw new InvalidOperationException($"Invalid period: {DateSelectionChosen!.PeriodChosen}");
                }

                var widthCss = width.HasValue ? $"{width}px" : "100%";

                GraphStyle = $"min-height: {height}px; width: {widthCss}; visibility: initial;";

                StateHasChanged();
            }
        }

        public async Task DateSelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;

            await SelectionChanged();
        }

        public class DisplayBaseData
        {
            // False for padding items (no measurements).
            public bool HasData { get; set; }
            public double ConsumptionKWh { get; set; }
            // Day: actual quarter-hour values; other periods: averages over the period.
            public double Temperature { get; set; }
            public double GlobalRadiation { get; set; }
            public double Humidity { get; set; }
        }

        public class ConsumptionDisplayDayData : DisplayBaseData
        {
            public DateTime Time { get; set; }
        }

        public class ConsumptionDisplayWeekData : DisplayBaseData
        {
            public int Day { get; set; }
            public string DayOfWeek { get; set; } = string.Empty;
            public int Position { get; internal set; }
        }

        public class ConsumptionDisplayMonthData : DisplayBaseData
        {
            public int Day { get; set; }
            public string DayOfWeek { get; set; } = string.Empty;
        }

        public class ConsumptionDisplayYearData : DisplayBaseData
        {
            public int Month { get; set; }
            public string MonthOfYear { get; set; } = string.Empty;
        }

        public class ConsumptionDisplayAllData : DisplayBaseData
        {
            public int Year { get; set; }
            public int Month { get; set; }
            public string MonthOfYear { get; set; } = string.Empty;
        }

        // Offset 0 (chosen period / most recent year) gets colour 0; variant is "line" or "area".
        private static string OffsetColor(int offset, string variant) => $"var(--sessy-year-{offset % 4}-{variant})";

        private string YearColor(int year, string variant) => OffsetColor(AllYears.Last() - year, variant);

        // Chosen period keeps the temperature colour; earlier years use their year colour.
        private static string TemperatureColor(int offset) => offset == 0 ? "var(--sessy-temperature-line)" : OffsetColor(offset, "line");

        private string ConsumptionTitle(int offset) => $"Consumption {CompareTitles[offset]}";

        private string TemperatureTitle(int offset) => $"Avg. temperature {CompareTitles[offset]}";

        public class ConsumptionDisplayCustomData : DisplayBaseData
        {
            public string X { get; set; } = string.Empty;
        }

        private async Task CompareChanged()
        {
            await SelectionChanged();
        }

        private async Task<List<ConsumptionRow>> LoadRange(DateTime start, DateTime end)
        {
            return await _consumptionDataService!.GetList(set => Task.FromResult(set
                .Where(c => start <= c.Time && c.Time < end)
                .OrderBy(c => c.Time)
                .ToList()));
        }

        private async Task<DateTime?> EarliestTime()
        {
            _earliestTime ??= await _consumptionDataService!.Query(set => Task.FromResult(set.Min(c => (DateTime?)c.Time)));

            return _earliestTime;
        }

        // Year offsets to compare with; stops once the period ends before the first measurement.
        private async Task<List<int>> CompareOffsets(Func<int, DateTime> periodEnd)
        {
            var earliest = await EarliestTime();
            var offsets = new List<int>();

            if (earliest == null)
                return offsets;

            var max = CompareMode switch
            {
                CompareModes.None => 0,
                CompareModes.PreviousYear => 1,
                _ => 50
            };

            for (var offset = 1; offset <= max && periodEnd(offset) > earliest; offset++)
                offsets.Add(offset);

            return offsets;
        }

        // Total of the same period one year back, cut off at the same point as the chosen period (now).
        // If data starts inside that period, only the overlapping part is compared.
        private async Task SetPreviousYearTotal(DateTime start, DateTime end, Func<DateTime, DateTime> toCurrent)
        {
            _previousYearKWh = null;
            _comparedFrom = null;

            var earliest = await EarliestTime();

            if (earliest == null || end <= earliest)
                return;

            var now = _timeZoneService!.Now;
            var previousStart = start;

            if (start < earliest)
            {
                previousStart = earliest.Value;
                _comparedFrom = toCurrent(previousStart);
            }

            var previous = await LoadRange(previousStart, end);

            if (previous.Count == 0)
                return;

            _previousYearKWh = previous.Where(r => toCurrent(r.Time) <= now).Sum(r => r.ConsumptionWh) / 4;

            var current = await LoadRange(toCurrent(previousStart), toCurrent(end));

            _comparedCurrentKWh = current.Sum(r => r.ConsumptionWh) / 4;
        }

        private static T Fill<T>(T item, List<ConsumptionRow> rows) where T : DisplayBaseData
        {
            if (rows.Count == 0)
                return item;

            item.HasData = true;
            item.ConsumptionKWh = rows.Sum(c => c.ConsumptionWh) / 4;
            item.Temperature = rows.Average(c => c.Temperature);
            item.GlobalRadiation = rows.Average(c => c.GlobalRadiation);
            item.Humidity = rows.Average(c => c.Humidity);

            return item;
        }

        private static string DayLabel(DateTime date) => $"{date.DayOfWeek.ToString().Substring(0, 2)} {date.Day}";

        private static string WeekTitle(DateTime start) => $"Week {ISOWeek.GetWeekOfYear(start)} {ISOWeek.GetYear(start)}";

        private static ConsumptionDisplayDayData DayItem(ConsumptionRow row, int shiftDays) => new()
        {
            HasData = true,
            Time = row.Time.AddDays(shiftDays),
            ConsumptionKWh = row.ConsumptionWh / 4,
            Temperature = row.Temperature,
            GlobalRadiation = row.GlobalRadiation,
            Humidity = row.Humidity
        };

        // 7 days from start, labelled with the days from labelStart (the chosen week).
        private static List<ConsumptionDisplayWeekData> BuildWeek(List<ConsumptionRow> rows, DateTime start, DateTime labelStart)
        {
            var byDate = rows.ToLookup(r => r.Time.Date);

            return Enumerable.Range(0, 7)
                .Select(i => Fill(new ConsumptionDisplayWeekData
                {
                    Day = labelStart.AddDays(i).Day,
                    DayOfWeek = DayLabel(labelStart.AddDays(i)),
                    Position = i
                }, byDate[start.AddDays(i)].ToList()))
                .ToList();
        }

        // 'count' days from start, labelled with the days from labelStart (the chosen month).
        private static List<ConsumptionDisplayMonthData> BuildDays(List<ConsumptionRow> rows, DateTime start, DateTime labelStart, int count)
        {
            var byDate = rows.ToLookup(r => r.Time.Date);

            return Enumerable.Range(0, count)
                .Select(i => Fill(new ConsumptionDisplayMonthData
                {
                    Day = i + 1,
                    DayOfWeek = DayLabel(labelStart.AddDays(i))
                }, byDate[start.AddDays(i)].ToList()))
                .ToList();
        }

        private static List<ConsumptionDisplayYearData> BuildMonths(List<ConsumptionRow> rows)
        {
            var byMonth = rows.ToLookup(r => r.Time.Month);

            return Enumerable.Range(1, 12)
                .Select(month => Fill(new ConsumptionDisplayYearData
                {
                    Month = month,
                    MonthOfYear = Formatters.FormatAsMonth(month)
                }, byMonth[month].ToList()))
                .ToList();
        }

        // Daily custom groups: X is the date shifted by shiftDays onto the chosen period.
        private static List<ConsumptionDisplayCustomData> BuildCustomDays(List<ConsumptionRow> rows, int shiftDays)
        {
            return rows.GroupBy(r => r.Time.Date)
                .OrderBy(gr => gr.Key)
                .Select(gr => Fill(new ConsumptionDisplayCustomData { X = gr.Key.AddDays(shiftDays).ToString("dd/MM/yyyy") }, gr.ToList()))
                .ToList();
        }

        // Monthly custom groups: X is the month shifted by shiftYears onto the chosen period.
        private static List<ConsumptionDisplayCustomData> BuildCustomMonths(List<ConsumptionRow> rows, int shiftYears)
        {
            return rows.GroupBy(r => new { r.Time.Year, r.Time.Month })
                .OrderBy(gr => gr.Key.Year)
                .ThenBy(gr => gr.Key.Month)
                .Select(gr => Fill(new ConsumptionDisplayCustomData { X = $"{gr.Key.Year + shiftYears}/{gr.Key.Month}" }, gr.ToList()))
                .ToList();
        }

        private void ClearCompare()
        {
            DayCompare = new();
            WeekPerYear = new();
            MonthCompare = new();
            YearPerYear = new();
            CustomCompare = new();
            CompareTitles = new();
            WeekWithData = new();
            YearWithData = new();
            ShownOffsets = new();
            _previousYearKWh = null;
        }

        private async Task SelectionChanged()
        {
            IsBusy = true;

            var dateChoosen = DateSelectionChosen?.DateChosen?.Date ?? _timeZoneService!.Now.Date;

            try
            {
                ClearCompare();

                switch (DateSelectionChosen!.PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        {
                            // Compare with the same weekday 52 weeks back.
                            var rows = await LoadRange(dateChoosen, dateChoosen.AddDays(1));

                            ConsumptionDayData = rows.Select(r => DayItem(r, 0)).ToList();
                            _totalConsumptionKWh = ConsumptionDayData.Sum(cd => cd.ConsumptionKWh);

                            foreach (var offset in await CompareOffsets(o => dateChoosen.AddDays(1 - 364 * o)))
                            {
                                var start = dateChoosen.AddDays(-364 * offset);
                                var previous = await LoadRange(start, start.AddDays(1));

                                if (previous.Count == 0)
                                    continue;

                                DayCompare[offset] = previous.Select(r => DayItem(r, 364 * offset)).ToList();
                                CompareTitles[offset] = start.ToString("ddd dd/MM/yyyy");
                            }

                            ShownOffsets = DayCompare.Keys.OrderByDescending(o => o).ToList();

                            await SetPreviousYearTotal(dateChoosen.AddDays(-364), dateChoosen.AddDays(-363), t => t.AddDays(364));

                            await ReloadCharts();

                            break;
                        }

                    case PeriodsEnums.Week:
                        {
                            // Compare with the week 52 weeks back, so weekdays line up.
                            var weekStart = dateChoosen.StartOfWeek().Date;
                            var rows = await LoadRange(weekStart, weekStart.AddDays(7));

                            WeekPerYear[0] = BuildWeek(rows, weekStart, weekStart);
                            CompareTitles[0] = WeekTitle(weekStart);
                            ConsumptionWeekData = WeekPerYear[0].Where(d => d.HasData).ToList();
                            _totalConsumptionKWh = ConsumptionWeekData.Sum(cd => cd.ConsumptionKWh);

                            foreach (var offset in await CompareOffsets(o => weekStart.AddDays(7 - 364 * o)))
                            {
                                var start = weekStart.AddDays(-364 * offset);
                                var previous = await LoadRange(start, start.AddDays(7));

                                if (previous.Count == 0)
                                    continue;

                                WeekPerYear[offset] = BuildWeek(previous, start, weekStart);
                                CompareTitles[offset] = WeekTitle(start);
                            }

                            ShownOffsets = WeekPerYear.Keys.OrderByDescending(o => o).ToList();
                            WeekWithData = WeekPerYear.ToDictionary(kv => kv.Key, kv => kv.Value.Where(d => d.HasData).ToList());

                            await SetPreviousYearTotal(weekStart.AddDays(-364), weekStart.AddDays(-357), t => t.AddDays(364));

                            await ReloadCharts();

                            break;
                        }

                    case PeriodsEnums.Month:
                        {
                            var monthStart = dateChoosen.StartOfMonth().Date;
                            var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
                            var rows = await LoadRange(monthStart, monthStart.AddMonths(1));

                            ConsumptionMonthBars = BuildDays(rows, monthStart, monthStart, days);
                            ConsumptionMonthData = ConsumptionMonthBars.Where(d => d.HasData).ToList();
                            _totalConsumptionKWh = ConsumptionMonthData.Sum(cd => cd.ConsumptionKWh);

                            foreach (var offset in await CompareOffsets(o => monthStart.AddMonths(1).AddYears(-o)))
                            {
                                var start = monthStart.AddYears(-offset);
                                var previous = await LoadRange(start, start.AddMonths(1));

                                if (previous.Count == 0)
                                    continue;

                                var count = Math.Min(days, DateTime.DaysInMonth(start.Year, start.Month));

                                MonthCompare[offset] = BuildDays(previous, start, monthStart, count).Where(d => d.HasData).ToList();
                                CompareTitles[offset] = $"{Formatters.FormatAsMonth(start.Month)} {start.Year}";
                            }

                            ShownOffsets = MonthCompare.Keys.OrderByDescending(o => o).ToList();

                            await SetPreviousYearTotal(monthStart.AddYears(-1), monthStart.AddYears(-1).AddMonths(1), t => t.AddYears(1));

                            await ReloadCharts();

                            break;
                        }

                    case PeriodsEnums.Year:
                        {
                            var yearStart = new DateTime(dateChoosen.Year, 1, 1);
                            var rows = await LoadRange(yearStart, yearStart.AddYears(1));

                            YearPerYear[0] = BuildMonths(rows);
                            CompareTitles[0] = yearStart.Year.ToString();
                            ConsumptionYearData = YearPerYear[0].Where(d => d.HasData).ToList();
                            _totalConsumptionKWh = ConsumptionYearData.Sum(cd => cd.ConsumptionKWh);

                            foreach (var offset in await CompareOffsets(o => yearStart.AddYears(1 - o)))
                            {
                                var start = yearStart.AddYears(-offset);
                                var previous = await LoadRange(start, start.AddYears(1));

                                if (previous.Count == 0)
                                    continue;

                                YearPerYear[offset] = BuildMonths(previous);
                                CompareTitles[offset] = start.Year.ToString();
                            }

                            ShownOffsets = YearPerYear.Keys.OrderByDescending(o => o).ToList();
                            YearWithData = YearPerYear.ToDictionary(kv => kv.Key, kv => kv.Value.Where(d => d.HasData).ToList());

                            await SetPreviousYearTotal(yearStart.AddYears(-1), yearStart, t => t.AddYears(1));

                            await ReloadCharts();

                            break;
                        }

                    case PeriodsEnums.All:
                        {
                            var result = await _consumptionDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .ToList();

                                return await Task.FromResult(result);
                            });
                            ConsumptionAllData = result.GroupBy(cd => new { cd.Time.Year, cd.Time.Month })
                                .OrderBy(gr => gr.Key.Year)
                                .ThenBy(gr => gr.Key.Month)
                                .Select(gr => new ConsumptionDisplayAllData
                                {
                                    Year = gr.Key.Year,
                                    Month = gr.Key.Month,
                                    MonthOfYear = Formatters.FormatAsMonth(gr.Key.Month),
                                    ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4,
                                    Temperature = gr.Average(cons => cons.Temperature),
                                    GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                    Humidity = gr.Average(cons => cons.Humidity)
                                })
                                .ToList();

                            AllYears = ConsumptionAllData.Select(cd => cd.Year).Distinct().OrderBy(year => year).ToList();

                            ConsumptionAllPerYear = AllYears.ToDictionary(year => year, year => Enumerable.Range(1, 12)
                                .Select(month => ConsumptionAllData.FirstOrDefault(cd => cd.Year == year && cd.Month == month)
                                    ?? new ConsumptionDisplayAllData { Year = year, Month = month, MonthOfYear = Formatters.FormatAsMonth(month) })
                                .ToList());

                            AllWithData = AllYears.ToDictionary(year => year, year => ConsumptionAllData.Where(cd => cd.Year == year).ToList());

                            ConsumptionAllWeather = result.GroupBy(cd => cd.Time.Month)
                                .OrderBy(gr => gr.Key)
                                .Select(gr => new ConsumptionDisplayAllData
                                {
                                    Month = gr.Key,
                                    MonthOfYear = Formatters.FormatAsMonth(gr.Key),
                                    Temperature = gr.Average(cons => cons.Temperature),
                                    GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                    Humidity = gr.Average(cons => cons.Humidity)
                                })
                                .ToList();

                            _totalConsumptionKWh  = ConsumptionAllData.Sum(cd => cd.ConsumptionKWh);

                            await ReloadCharts();

                            break;
                        }

                    case PeriodsEnums.Custom:
                        {
                            var start = DateSelectionChosen.Start!.Value;
                            var end = DateSelectionChosen.End!.Value;
                            var rows = await LoadRange(start, end);

                            switch (DateSelectionChosen.DurationChosen)
                            {
                                case DurationEnums.Last7Days:
                                case DurationEnums.Last30Days:
                                    {
                                        // Daily: compare 52 weeks back, so weekdays line up.
                                        ConsumptionCustomData = BuildCustomDays(rows, 0);

                                        foreach (var offset in await CompareOffsets(o => end.AddDays(-364 * o)))
                                        {
                                            var previous = await LoadRange(start.AddDays(-364 * offset), end.AddDays(-364 * offset));

                                            if (previous.Count == 0)
                                                continue;

                                            CustomCompare[offset] = BuildCustomDays(previous, 364 * offset);
                                            CompareTitles[offset] = $"{start.AddDays(-364 * offset):dd/MM/yyyy} - {end.AddDays(-364 * offset - 1):dd/MM/yyyy}";
                                        }

                                        await SetPreviousYearTotal(start.AddDays(-364), end.AddDays(-364), t => t.AddDays(364));

                                        break;
                                    }

                                case DurationEnums.Last90Days:
                                case DurationEnums.Last180Days:
                                case DurationEnums.Last365Days:
                                    {
                                        // Monthly: compare the same months one or more years back.
                                        ConsumptionCustomData = BuildCustomMonths(rows, 0);

                                        foreach (var offset in await CompareOffsets(o => end.AddYears(-o)))
                                        {
                                            var previous = await LoadRange(start.AddYears(-offset), end.AddYears(-offset));

                                            if (previous.Count == 0)
                                                continue;

                                            CustomCompare[offset] = BuildCustomMonths(previous, offset);
                                            CompareTitles[offset] = $"{start.AddYears(-offset):dd/MM/yyyy} - {end.AddYears(-offset).AddDays(-1):dd/MM/yyyy}";
                                        }

                                        await SetPreviousYearTotal(start.AddYears(-1), end.AddYears(-1), t => t.AddYears(1));

                                        break;
                                    }

                                default:
                                    throw new InvalidOperationException($"Invalid duration: {DateSelectionChosen.DurationChosen}");
                            }

                            ShownOffsets = CustomCompare.Keys.OrderByDescending(o => o).ToList();
                            _totalConsumptionKWh = ConsumptionCustomData.Sum(cd => cd.ConsumptionKWh);

                            await ReloadCharts();

                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Invalid period: {DateSelectionChosen!.PeriodChosen}");
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ReloadCharts()
        {
            DetermineTickDistance();

            HandleScreenHeight();

            FillFormatter();

            switch (DateSelectionChosen!.PeriodChosen)
            {
                case PeriodsEnums.Day:
                    await ConsumptionDayChart!.Reload();
                    break;

                case PeriodsEnums.Week:
                    await ConsumptionWeekChart!.Reload();
                    break;

                case PeriodsEnums.Month:
                    await ConsumptionMonthChart!.Reload();
                    break;

                case PeriodsEnums.Year:
                    await ConsumptionYearChart!.Reload();
                    break;

                case PeriodsEnums.All:
                    await ConsumptionAllChart!.Reload();
                    break;

                case PeriodsEnums.Custom:
                    await ConsumptionCustomChart!.Reload();
                    break;

                default:
                    throw new InvalidOperationException($"Invalid period: {DateSelectionChosen!.PeriodChosen}");
            }

            StateHasChanged();
        }

        private void DetermineTickDistance()
        {
            TickDistance = ConsumptionChartWidth;

            switch (DateSelectionChosen!.PeriodChosen)
            {
                case PeriodsEnums.Day:
                    {
                        if (ConsumptionDayData.Count > 0)
                        {
                            var start = ConsumptionDayData.Min(list => list.Time).DateFloorQuarter();
                            var end = ConsumptionDayData.Max(list => list.Time).AddDays(1).DateCeilingQuarter();
                            var quarters = (end - start).Hours * 4;

                            TickDistance = ConsumptionChartWidth / (quarters == 0 ? 96 : quarters);
                        }

                        break;
                    }

                case PeriodsEnums.Week:
                    {
                        TickDistance = ConsumptionChartWidth / 14;

                        break;
                    }

                case PeriodsEnums.Month:
                    {
                        var days = ConsumptionMonthBars.Count;

                        TickDistance = ConsumptionChartWidth / (days == 0 ? 31 : days * 4);

                        break;
                    }

                case PeriodsEnums.Year:
                    {
                        TickDistance = ConsumptionChartWidth / 24;

                        break;
                    }

                case PeriodsEnums.All:
                    {
                        TickDistance = ConsumptionChartWidth / 24;

                        break;
                    }

                case PeriodsEnums.Custom:
                    {
                        switch (DateSelectionChosen.DurationChosen)
                        {
                            case DurationEnums.Last7Days:
                                TickDistance = ConsumptionChartWidth / 14;
                                break;
                            case DurationEnums.Last30Days:
                                TickDistance = ConsumptionChartWidth / 150;
                                break;
                            case DurationEnums.Last90Days:
                                TickDistance = ConsumptionChartWidth / 180; 
                                break;
                            case DurationEnums.Last180Days:
                                TickDistance = ConsumptionChartWidth / 360;
                                break;
                            case DurationEnums.Last365Days:
                                TickDistance = ConsumptionChartWidth / 730;
                                break;
                            default:
                                break;
                        }

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Invalid period: {DateSelectionChosen!.PeriodChosen}");
            }

            StateHasChanged();
        }

        private Func<object, string>? Formatter { get; set; } = null;
        public int ConsumptionChartWidth { get; private set; }

        public void FillFormatter()
        {
            switch (DateSelectionChosen!.PeriodChosen)
            {
                case PeriodsEnums.Day:
                    Formatter = Formatters.FormatAsDayHourMinutes;
                    break;

                case PeriodsEnums.Week:
                case PeriodsEnums.Month:
                    Formatter = Formatters.FormatValue;
                    break;

                case PeriodsEnums.Custom:
                case PeriodsEnums.Year:
                case PeriodsEnums.All:
                    Formatter = null;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid PeriodChosen: {DateSelectionChosen!.PeriodChosen}");
            }
        }
    }
}
