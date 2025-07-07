using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Extensions;
using SessyController.Services;
using SessyData.Model;
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

        public DateTime? DateChosen { get; set; }
        public PeriodsEnums PeriodChosen { get; set; }

        private RadzenChart? ConsumptionChart { get; set; }
        private RadzenChart? HumidityChart { get; set; }
        private RadzenChart? GlobalRadiationChart { get; set; }
        private RadzenChart? TemperatureChart { get; set; }

        private string GraphStyle { get; set; } = "width: 100%; height: 60vh";

        private int TickDistance { get; set; }

        public List<ConsumptionDisplayData> ConsumptionData { get; set; } = new();

        protected override async Task OnInitializedAsync()
        {

            await base.OnInitializedAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                DateChosen = _timeZoneService!.Now.Date;
                PeriodChosen = PeriodsEnums.Month;

                await SelectionChanged();
            }

            await base.OnAfterRenderAsync(firstRender);
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

        public class ConsumptionDisplayData
        {
            public DateTime Time { get; set; }
            public double ConsumptionKWh { get; set; }
            public double Temperature { get; set; }
            public double GlobalRadiation { get; set; }
            public double Humidity { get; set; }
        }

        private async Task SelectionChanged()
        {
            ConsumptionChartWidth = await _screenSizeService!.GetElementWidth(ConsumptionChart!.Element);

            DateChosen ??= DateChosen?.Date ?? _timeZoneService!.Now.Date;

            var list = _consumptionDataService!.GetList((set) =>
            {
                switch (PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        return set
                            .Where(sed => sed.Time.Date == DateChosen)
                            .ToList();

                    case PeriodsEnums.Week:
                        return set
                            .Where(sed => DateChosen!.Value.StartOfWeek() <= sed.Time &&
                                          DateChosen!.Value.EndOfWeek().AddDays(1).AddSeconds(-1) >= sed.Time)
                            .ToList();

                    case PeriodsEnums.Month:
                        return set
                            .Where(sed => DateChosen!.Value.StartOfMonth() <= sed.Time &&
                                          DateChosen!.Value.EndOfMonth().AddDays(1).AddSeconds(-1) >= sed.Time)
                            .ToList();

                    case PeriodsEnums.Year:
                        return set
                            .Where(sed => DateChosen!.Value.Year == sed.Time.Year)
                            .ToList();

                    case PeriodsEnums.All:
                        return set.ToList();

                    default:
                        throw new InvalidOperationException($"Wrong period type {PeriodChosen}");
                }
            }).OrderBy(sed => sed.Time)
              .ToList();

            ConsumptionData = list.Select(cd => new ConsumptionDisplayData
            {
                Time = cd.Time,
                ConsumptionKWh = cd.ConsumptionKWh,
                Humidity = cd.Humidity,
                GlobalRadiation = cd.GlobalRadiation,
                Temperature = cd.Temperature
            }).ToList();

            DetermineTickDistance(ConsumptionData);

            FillFormatter();

            StateHasChanged();

            await ConsumptionChart!.Reload();
            await HumidityChart!.Reload();
            await GlobalRadiationChart!.Reload();
            await TemperatureChart!.Reload();
        }

        private void DetermineTickDistance(List<ConsumptionDisplayData>? consumptionData)
        {
            TickDistance = ConsumptionChartWidth;

            if (consumptionData != null && consumptionData.Count > 0)
            {
                var start = consumptionData.Min(list => list.Time).DateFloorQuarter();
                var end = consumptionData.Max(list => list.Time).AddDays(1).DateCeilingQuarter();

                switch (PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        {
                            var quarters = (end - start).Hours * 4;

                            TickDistance = ConsumptionChartWidth / (quarters == 0 ? 96 : quarters);

                            break;
                        }

                    case PeriodsEnums.Week:
                        {
                            var days = (end.Date - start.Date).Days;

                            TickDistance = ConsumptionChartWidth / (days == 0 ? 7 : days);

                            break;
                        }

                    case PeriodsEnums.Month:
                        {
                            var days = (end.Date - start.Date).Days;

                            TickDistance = ConsumptionChartWidth / (days == 0 ? 31 : days);

                            break;
                        }

                    case PeriodsEnums.Year:
                    case PeriodsEnums.All:
                        {
                            var months = end.Month - start.Month + 1;

                            TickDistance = ConsumptionChartWidth / (months == 0 ? 12 : months);

                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Invalid period: {PeriodChosen}");
                }
            }
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
                    Formatter = Formatters.FormatAsDay;
                    break;

                case PeriodsEnums.Year:
                case PeriodsEnums.All:
                    Formatter = Formatters.FormatAsMonth;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid PeriodChosen: {PeriodChosen}");
            }
        }
    }
}
