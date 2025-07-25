using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Extensions;
using SessyController.Services;
using SessyData.Services;
using SessyWeb.Helpers;
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

        public DateTime? DateChosen { get; set; }
        public PeriodsEnums PeriodChosen { get; set; }

        private RadzenChart? ConsumptionDayChart { get; set; }
        private RadzenChart? ConsumptionWeekChart { get; set; }
        private RadzenChart? ConsumptionMonthChart { get; set; }
        private RadzenChart? ConsumptionYearChart { get; set; }
        private RadzenChart? ConsumptionAllChart { get; set; }

        private RadzenChart? HumidityChart { get; set; }
        private RadzenChart? GlobalRadiationChart { get; set; }
        private RadzenChart? TemperatureChart { get; set; }

        private string GraphStyle { get; set; } = "width: 100%; height: 60vh";

        private int TickDistance { get; set; }

        public List<ConsumptionDisplayDayData> ConsumptionDayData { get; set; } = new();
        public List<ConsumptionDisplayWeekData> ConsumptionWeekData { get; set; } = new();
        public List<ConsumptionDisplayMonthData> ConsumptionMonthData { get; set; } = new();
        public List<ConsumptionDisplayYearData> ConsumptionYearData { get; set; } = new();
        public List<ConsumptionDisplayAllData> ConsumptionAllData { get; set; } = new();

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                DateChosen = _timeZoneService!.Now.Date;
                PeriodChosen = PeriodsEnums.Month;
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

            HandleResize(height - 300, width);
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
            // 25 pixels per data row
            var width = 1000;

            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    width = ConsumptionDayData.Count * 35;
                    break;

                case PeriodsEnums.Week:
                    width = ConsumptionWeekData.Count * 250;
                    break;

                case PeriodsEnums.Month:
                    width = ConsumptionMonthData.Count * 60;
                    break;

                case PeriodsEnums.Year:
                    width = ConsumptionYearData.Count * 250;
                    break;

                case PeriodsEnums.All:
                    width = ConsumptionAllData.Count * 500;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid period: {PeriodChosen}");
            }

            GraphStyle = $"min-height: {height}px; width: {width}px; visibility: initial;";

            StateHasChanged();
        }


        public async Task PeriodChosenChanged(PeriodsEnums period)
        {
            PeriodChosen = period;

            await SelectionChanged();
        }

        public async Task DateChosenChanged(DateTime date)
        {
            DateChosen = date;

            await SelectionChanged();
        }

        public class ConsumptionDisplayDayData
        {
            public DateTime Time { get; set; }
            public double ConsumptionKWh { get; set; }
            public double Temperature { get; set; }
            public double GlobalRadiation { get; set; }
            public double Humidity { get; set; }
        }

        public class ConsumptionDisplayWeekData
        {
            public int Day { get; set; }
            public string DayOfWeek { get; set; } = string.Empty;
            public double ConsumptionKWh { get; set; }
        }

        public class ConsumptionDisplayMonthData
        {
            public int Day { get; set; }
            public string DayOfWeek { get; set; } = string.Empty;
            public double ConsumptionKWh { get; set; }
        }

        public class ConsumptionDisplayYearData
        {
            public int Month { get; set; }
            public string MonthOfYear { get; set; } = string.Empty;
            public double ConsumptionKWh { get; set; }
        }

        public class ConsumptionDisplayAllData
        {
            public string Year { get; set; } = string.Empty;
            public double ConsumptionKWh { get; set; }
        }

        private async Task SelectionChanged()
        {
            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    {
                        var list = _consumptionDataService!.GetList((set) =>
                        {
                            return set
                                .Where(sed => sed.Time.Date == DateChosen)
                                .ToList();
                        });

                        ConsumptionDayData = list.Select(cd => new ConsumptionDisplayDayData
                        {
                            Time = cd.Time,
                            ConsumptionKWh = cd.ConsumptionWh,
                            Humidity = cd.Humidity,
                            GlobalRadiation = cd.GlobalRadiation,
                            Temperature = cd.Temperature
                        }).ToList();

                        await ReloadCharts();

                        break;
                    }


                case PeriodsEnums.Week:
                    {
                        ConsumptionWeekData = _consumptionDataService!.GetList((set) =>
                        {
                            return set
                            .Where(sed => DateChosen!.Value.StartOfWeek() <= sed.Time &&
                                          DateChosen!.Value.EndOfWeek().AddDays(1).AddSeconds(-1) >= sed.Time)
                            .ToList();
                        }).GroupBy(cd => cd.Time.Date)
                            .Select(gr => new ConsumptionDisplayWeekData
                            {
                                Day = gr.Key.Day,
                                DayOfWeek = $"{gr.Key.DayOfWeek.ToString().Substring(0, 2)} {gr.Key.Day}",
                                ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4
                            })
                            .OrderBy(item => item.Day)
                            .ToList();

                        await ReloadCharts();

                        break;
                    }

                case PeriodsEnums.Month:
                    {
                        ConsumptionMonthData = _consumptionDataService!.GetList((set) =>
                        {
                            return set
                                .Where(sed => DateChosen!.Value.StartOfMonth() <= sed.Time &&
                                              DateChosen!.Value.EndOfMonth().AddDays(1).AddSeconds(-1) >= sed.Time)
                                .ToList();
                        }).GroupBy(cd => cd.Time.Date)
                            .Select(gr => new ConsumptionDisplayMonthData
                            {
                                Day = gr.Key.Day,
                                DayOfWeek = $"{gr.Key.DayOfWeek.ToString().Substring(0, 2)} {gr.Key.Day}",
                                ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4
                            })
                            .OrderBy(item => item.Day)
                            .ToList();

                        await ReloadCharts();

                        break;
                    }

                case PeriodsEnums.Year:
                    {
                        ConsumptionYearData = _consumptionDataService!.GetList((set) =>
                        {
                            return set
                                .Where(sed => DateChosen!.Value.Year == sed.Time.Year)
                                .ToList();
                        }).GroupBy(cd => cd.Time.Month)
                            .Select(gr => new ConsumptionDisplayYearData
                            {
                                Month = gr.Key,
                                MonthOfYear = Formatters.FormatAsMonth(gr.Key),
                                ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4
                            })
                            .ToList();

                        await ReloadCharts();

                        break;
                    }

                case PeriodsEnums.All:
                    {
                        ConsumptionAllData = _consumptionDataService!.GetList((set) =>
                        {
                            return set
                                .ToList();
                        }).GroupBy(cd => cd.Time.Year)
                            .Select(gr => new ConsumptionDisplayAllData
                            {
                                Year = gr.Key.ToString(),
                                ConsumptionKWh = gr.Sum(cons => cons.ConsumptionWh) / 4
                            })
                            .ToList();

                        await ReloadCharts();

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Invalid period: {PeriodChosen}");
            }
        }

        private async Task ReloadCharts()
        {
            DetermineTickDistance();

            HandleScreenHeight();

            FillFormatter();

            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    await ConsumptionDayChart!.Reload();
                    await HumidityChart!.Reload();
                    await GlobalRadiationChart!.Reload();
                    await TemperatureChart!.Reload();
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

                default:
                    throw new InvalidOperationException($"Invalid period: {PeriodChosen}");
            }

            StateHasChanged();
        }

        private void DetermineTickDistance()
        {
            TickDistance = ConsumptionChartWidth;

            switch (PeriodChosen)
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
                        var years = ConsumptionAllData.Count;

                        TickDistance = ConsumptionChartWidth / (years == 0 ? 1 : years * 500);

                        break;
                    }

                default:
                    throw new InvalidOperationException($"Invalid period: {PeriodChosen}");
            }

            StateHasChanged();
        }

        private Func<object, string>? Formatter { get; set; } = null;
        public int ConsumptionChartWidth { get; private set; }

        public void FillFormatter()
        {
            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    Formatter = Formatters.FormatAsDayHourMinutes;
                    break;

                case PeriodsEnums.Week:
                case PeriodsEnums.Month:
                    Formatter = Formatters.FormatValue;
                    break;

                case PeriodsEnums.Year:
                case PeriodsEnums.All:
                    Formatter = null;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid PeriodChosen: {PeriodChosen}");
            }
        }
    }
}
