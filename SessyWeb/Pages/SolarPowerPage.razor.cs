using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Helpers;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class SolarPowerPage : PageBase
    {
        [Inject]
        SolarEdgeDataService? _solarEdgeDataService { get; set; }

        [Inject]
        TimeZoneService? _timeZoneService { get; set; }

        [Inject]
        SolarService? _solarService { get; set; }

        private Dictionary<string, List<SolarInverterData>> GroupedData { get; set; } = new();
        private List<string> providerNames { get; set; } = new();
        public List<SolarInverterData> SolarInverterData { get; set; } = new();

        public string selectedProvider { get; set; } = "All";

        public DateTime? DateChosen { get; set; }

        public PeriodsEnums PeriodChosen { get; set; }

        public double SolarPower { get; set; }

        private string GraphStyle { get; set; } = "width: 100%; height: 60vh";

        private RadzenChart? SolarPowerChart { get; set; }

        private int SolarPowerChartWidth { get; set; } = 2000;

        private int TickDistance { get; set; }

        protected override async Task OnInitializedAsync()
        {
            DateChosen = _timeZoneService!.Now.Date;
            PeriodChosen = PeriodsEnums.Month;

            await base.OnInitializedAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
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

        private Func<object, string>? Formatter { get; set; } = null;

        public void FillFormatter()
        {
            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    Formatter = Formatters.FormatAsDayHour;
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

        public override void ScreenInfoChanged(ScreenInfo screenInfo)
        {
            SolarPowerChartWidth = ScreenInfo!.Width == 0 ? 2300 : ScreenInfo!.Width;

            base.ScreenInfoChanged(screenInfo);
        }

        private async Task SelectionChanged()
        {
            DateChosen ??= DateChosen?.Date ?? _timeZoneService!.Now.Date;

            SolarInverterData = _solarEdgeDataService!.GetList((set) =>
            {
                switch (PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        return set
                            .Where(sed => sed.Time.Date == DateChosen)
                            .ToList();

                    case PeriodsEnums.Week:
                        return set
                            .Where(sed => DateChosen!.Value.StartOfWeek() <= sed.Time && DateChosen!.Value.EndOfWeek().AddDays(1).AddSeconds(-1) >= sed.Time)
                            .ToList();

                    case PeriodsEnums.Month:
                        return set
                            .Where(sed => DateChosen!.Value.StartOfMonth() <= sed.Time && DateChosen!.Value.EndOfMonth().AddDays(1).AddSeconds(-1) >= sed.Time)
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
              .ToList(); ;

            SolarPower = GetSolarPower(DateChosen!.Value);

            GroupedData = SolarInverterData
                    .GroupBy(d => d.ProviderName)
                    .ToDictionary(g => g.Key, g => g.ToList());

            providerNames = GroupedData.Keys.OrderBy(k => k).ToList();
            providerNames.Insert(0, "All");

            var numberOfElements = GroupedData.Values.Count();

            DetermineTickDistance(GroupedData);

            FillFormatter();

            StateHasChanged();

            await SolarPowerChart!.Reload();
        }

        private void DetermineTickDistance(Dictionary<string, List<SolarInverterData>> groupedData)
        {
            TickDistance = SolarPowerChartWidth;
            
            if (groupedData != null && groupedData.Count > 0)
            {
                var start = groupedData.Values.Min(list => list.Min(sid => sid.Time));
                var end = groupedData.Values.Max(list => list.Max(sid => sid.Time)).AddDays(1);

                switch (PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        {
                            var hours = (end - start).Hours;

                            TickDistance = SolarPowerChartWidth / (hours == 0 ? 24 : hours);

                            break;
                        }

                    case PeriodsEnums.Week:
                        {
                            var days = (end - start).Days;

                            TickDistance = SolarPowerChartWidth / (days == 0 ? 7 : days);

                            break;
                        }

                    case PeriodsEnums.Month:
                        {
                            var days = (end - start).Days;

                            TickDistance = SolarPowerChartWidth / (days == 0 ? 31 : days);
                            break;
                        }

                    case PeriodsEnums.Year:
                    case PeriodsEnums.All:
                        {
                            var months = (end - start).Days / 30;

                            TickDistance = SolarPowerChartWidth / (months == 0 ? 12 : months);

                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Invalid period: {PeriodChosen}");
                }
            }
        }

        private double GetSolarPower(DateTime date)
        {
            DateTime start;
            DateTime end;

            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                    start = date.Date;
                    end = start.AddDays(1).AddSeconds(-1);
                    break;

                case PeriodsEnums.Week:
                    start = date.StartOfWeek();
                    end = date.EndOfWeek().AddDays(1).AddSeconds(-1);
                    break;

                case PeriodsEnums.Month:
                    start = date.StartOfMonth();
                    end = date.EndOfMonth().AddDays(1).AddSeconds(-1);
                    break;

                case PeriodsEnums.Year:
                    start = new DateTime(date.Year, 1, 1);
                    end = new DateTime(date.Year, 12, 31, 23, 59, 59);
                    break;

                case PeriodsEnums.All:
                    start = DateTime.MinValue;
                    end = DateTime.MaxValue;
                    break;

                default:
                    throw new InvalidOperationException($"Wrong period chosen {PeriodChosen}");
            }

            return _solarService!.GetRealizedSolarPower(start, end);
        }
    }
}
