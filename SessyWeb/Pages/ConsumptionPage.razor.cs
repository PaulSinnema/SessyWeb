using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services;
using SessyData.Services;
using SessyWeb.Helpers;
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
                        width = ConsumptionDayData.Count * 35;
                        break;

                    case PeriodsEnums.Week:
                        width = ConsumptionWeekData.Count * 250;
                        break;

                    case PeriodsEnums.Month:
                        // Fixed bar width based on days in month, not days loaded yet, so a partial month doesn't look squeezed.
                        var dateChoosen = DateSelectionChosen.DateChosen?.Date ?? _timeZoneService!.Now.Date;
                        width = DateTime.DaysInMonth(dateChoosen.Year, dateChoosen.Month) * 60;
                        break;

                    case PeriodsEnums.Year:
                        width = ConsumptionYearData.Count * 250;
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

        // Most recent year gets colour 0; variant is "line" or "area".
        private string YearColor(int year, string variant) => $"var(--sessy-year-{(AllYears.Count - 1 - AllYears.IndexOf(year)) % 4}-{variant})";

        public class ConsumptionDisplayCustomData : DisplayBaseData
        {
            public string X { get; set; } = string.Empty;
        }

        private async Task SelectionChanged()
        {
            IsBusy = true;

            var dateChoosen = DateSelectionChosen?.DateChosen?.Date ?? _timeZoneService!.Now.Date;

            try
            {
                switch (DateSelectionChosen!.PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        {
                            var list = await _consumptionDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => sed.Time.Date == dateChoosen)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            ConsumptionDayData = list.Select(cd => new ConsumptionDisplayDayData
                            {
                                Time = cd.Time,
                                ConsumptionKWh = cd.ConsumptionWh / 4,
                                Humidity = cd.Humidity,
                                GlobalRadiation = cd.GlobalRadiation,
                                Temperature = cd.Temperature
                            }).ToList();

                            _totalConsumptionKWh = ConsumptionDayData.Sum(cd => cd.ConsumptionKWh);

                            await ReloadCharts();

                            break;
                        }


                    case PeriodsEnums.Week:
                        {
                            var result = await _consumptionDataService!.GetList(async (set) =>
                            {
                                var start = dateChoosen.StartOfWeek();
                                var end = dateChoosen.EndOfWeek().AddDays(1).AddSeconds(-1);

                                var result = set
                                    .Where(sed => start <= sed.Time &&
                                                  end >= sed.Time)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            ConsumptionWeekData = result.GroupBy(cd => cd.Time.Date)
                                .Select(gr => new ConsumptionDisplayWeekData
                                {
                                    Day = gr.Key.Day,
                                    DayOfWeek = $"{gr.Key.DayOfWeek.ToString().Substring(0, 2)} {gr.Key.Day}",
                                    Position = (int)gr.Key.DayOfWeek,
                                    ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4,
                                    Temperature = gr.Average(cons => cons.Temperature),
                                    GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                    Humidity = gr.Average(cons => cons.Humidity)
                                })
                                .OrderBy(item => item.Position)
                                .ToList();

                            _totalConsumptionKWh  = ConsumptionWeekData.Sum(cd => cd.ConsumptionKWh);

                            await ReloadCharts();

                            break;
                        }

                    case PeriodsEnums.Month:
                        {
                            var result = await _consumptionDataService!.GetList(async (set) =>
                            {
                                var start = dateChoosen.EndOfMonth().AddDays(1).AddSeconds(-1);

                                var result = set
                                    .Where(sed => dateChoosen.StartOfMonth() <= sed.Time &&
                                                  dateChoosen.EndOfMonth().AddDays(1).AddSeconds(-1) >= sed.Time)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            ConsumptionMonthData = result.GroupBy(cd => cd.Time.Date)
                                .Select(gr => new ConsumptionDisplayMonthData
                                {
                                    Day = gr.Key.Day,
                                    DayOfWeek = $"{gr.Key.DayOfWeek.ToString().Substring(0, 2)} {gr.Key.Day}",
                                    ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4,
                                    Temperature = gr.Average(cons => cons.Temperature),
                                    GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                    Humidity = gr.Average(cons => cons.Humidity)
                                })
                                .OrderBy(item => item.Day)
                                .ToList();

                            _totalConsumptionKWh  = ConsumptionMonthData.Sum(cd => cd.ConsumptionKWh);

                            await ReloadCharts();

                            break;
                        }

                    case PeriodsEnums.Year:
                        {
                            var result = await _consumptionDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => dateChoosen.Year == sed.Time.Year)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            ConsumptionYearData = result.GroupBy(cd => cd.Time.Month)
                                .Select(gr => new ConsumptionDisplayYearData
                                {
                                    Month = gr.Key,
                                    MonthOfYear = Formatters.FormatAsMonth(gr.Key),
                                    ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4,
                                    Temperature = gr.Average(cons => cons.Temperature),
                                    GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                    Humidity = gr.Average(cons => cons.Humidity)
                                })
                                .ToList();

                            _totalConsumptionKWh  = ConsumptionYearData.Sum(cd => cd.ConsumptionKWh);

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
                            var result = await _consumptionDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => sed.Time >= DateSelectionChosen.Start && sed.Time < DateSelectionChosen.End)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            switch (DateSelectionChosen.DurationChosen)
                            {
                                case DurationEnums.Last7Days:
                                    ConsumptionCustomData = result.GroupBy(cd => cd.Time.Date)
                                        .Select(gr => new ConsumptionDisplayCustomData
                                        {
                                            X = gr.Key.ToString("dd/MM/yyyy"),
                                            ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4,
                                            Temperature = gr.Average(cons => cons.Temperature),
                                            GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                            Humidity = gr.Average(cons => cons.Humidity)
                                        })
                                        .ToList();
                                    break;

                                case DurationEnums.Last30Days:
                                    ConsumptionCustomData = result.GroupBy(cd => cd.Time.Date)
                                        .Select(gr => new ConsumptionDisplayCustomData
                                        {
                                            X = gr.Key.ToString("dd/MM/yyyy"),
                                            ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4,
                                            Temperature = gr.Average(cons => cons.Temperature),
                                            GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                            Humidity = gr.Average(cons => cons.Humidity)
                                        })
                                        .ToList();
                                    break;

                                case DurationEnums.Last90Days:
                                case DurationEnums.Last180Days:
                                case DurationEnums.Last365Days:
                                    ConsumptionCustomData = result.GroupBy(cd => $"{cd.Time.Date.Year}/{cd.Time.Date.Month}")
                                        .Select(gr => new ConsumptionDisplayCustomData
                                        {
                                            X = gr.Key,
                                            ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4,
                                            Temperature = gr.Average(cons => cons.Temperature),
                                            GlobalRadiation = gr.Average(cons => cons.GlobalRadiation),
                                            Humidity = gr.Average(cons => cons.Humidity)
                                        })
                                        .ToList();
                                    break;


                                default:
                                    throw new InvalidOperationException($"Invalid duration: {DateSelectionChosen.DurationChosen}");
                            }

                            _totalConsumptionKWh  = ConsumptionCustomData.Sum(cd => cd.ConsumptionKWh);

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
                        var days = ConsumptionWeekData.Count;

                        TickDistance = ConsumptionChartWidth / (days == 0 ? 8 : days * 2);

                        break;
                    }

                case PeriodsEnums.Month:
                    {
                        var days = ConsumptionMonthData.Count;

                        TickDistance = ConsumptionChartWidth / (days == 0 ? 31 : days * 4);

                        break;
                    }

                case PeriodsEnums.Year:
                    {
                        var months = ConsumptionYearData.Count;

                        TickDistance = ConsumptionChartWidth / (months == 0 ? 12 : months * 2);

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
