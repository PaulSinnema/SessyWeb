using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.Blazor.Rendering;
using SessyCommon.Services;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Helpers;
using System.Linq;
using static SessyWeb.Components.DateChooserComponent;

namespace SessyWeb.Pages
{
    public partial class SolarPowerPage : PageBase
    {
        [Inject]
        SolarInverterDataService? _solarEdgeDataService { get; set; }

        [Inject]
        TimeZoneService? _timeZoneService { get; set; }

        [Inject]
        SolarService? _solarService { get; set; }

        private List<string> ProviderNames { get; set; } = new();
        public List<SolarInverterData> SolarInverterData { get; set; } = new();

        public string SelectedProvider { get; set; } = "All";

        public double SolarPower { get; set; }

        private string GraphStyle { get; set; } = "width: 100%; height: 60vh";

        private RadzenChart? SolarPowerChart { get; set; }

        private int SolarPowerChartWidth { get; set; } = 2000;

        private int TickDistance { get; set; }

        public Dictionary<string, Dictionary<DateTime, double>> SolarDayData { get; set; } = new();
        public Dictionary<string, Dictionary<DateTime, double>> SolarWeekData { get; set; } = new();
        public Dictionary<string, Dictionary<DateTime, double>> SolarMonthData { get; set; } = new();
        public Dictionary<string, Dictionary<int, double>> SolarYearData { get; set; } = new();
        public Dictionary<string, Dictionary<int, double>> SolarAllData { get; set; } = new();

        protected override async Task OnInitializedAsync()
        {
            DateSelectionChosen = new DateArgs(PeriodsEnums.Day, _timeZoneService!.Now.Date);

            await base.OnInitializedAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender || _screenInfoChanged)
            {
                await SelectionChanged();

                _screenInfoChanged = false;
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        public async Task DateSelectionChanged(DateArgs dateArgs)
        {
            DateSelectionChosen = dateArgs;

            await SelectionChanged();
        }

        private Func<object, string>? Formatter { get; set; } = null;
        public DateArgs? DateSelectionChosen { get; private set; }

        public void FillFormatter()
        {
            switch (DateSelectionChosen!.PeriodChosen)
            {
                case PeriodsEnums.Day:
                    Formatter = Formatters.FormatAsDayHour;
                    break;

                case PeriodsEnums.Week:
                case PeriodsEnums.Month:
                    Formatter = Formatters.FormatAsDay;
                    break;

                case PeriodsEnums.Year:
                    Formatter = Formatters.FormatAsMonth;
                    break;

                case PeriodsEnums.All:
                    Formatter = Formatters.FormatAsYear;
                    break;

                default:
                    throw new InvalidOperationException($"Invalid PeriodChosen: {DateSelectionChosen!.PeriodChosen}");
            }
        }

        private bool _screenInfoChanged = false;

        public override void ScreenInfoChanged(ScreenInfo screenInfo)
        {
            SolarPowerChartWidth = ScreenInfo!.Width == 0 ? 2300 : ScreenInfo!.Width;

            _screenInfoChanged = true;

            base.ScreenInfoChanged(screenInfo);
        }

        public async Task ProviderChanged()
        {
            await SelectionChanged();
        }

        private async Task SelectionChanged()
        {
            IsBusy = true;

            try
            {
                var DateChosen = DateSelectionChosen!.DateChosen?.Date ?? _timeZoneService!.Now.Date;

                switch (DateSelectionChosen!.PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        {
                            var result = await _solarEdgeDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => sed.Time.Date == DateChosen.Date)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            SolarDayData = new Dictionary<string, Dictionary<DateTime, double>>();

                            ProviderNames = result.Select(sid => sid.ProviderName).Distinct().ToList();

                            foreach (var providerName in ProviderNames)
                            {
                                var dateData = new Dictionary<DateTime, double>();

                                SolarDayData.Add(providerName, dateData);

                                var dates = result.Select(sid => sid.Time).Distinct();

                                foreach (var quarters in dates)
                                {
                                    dateData.Add(quarters, result.Where(sid => sid.ProviderName == providerName && sid.Time == quarters).Sum(sid => sid.Power) / 4000);
                                }
                            }

                            DetermineTickDistance(result);
                        }
                        //{
                        //    var result = await _solarEdgeDataService!.GetList(async (set) =>
                        //    {
                        //        var result = set
                        //            .Where(sed => sed.Time.Date == DateChosen.Date)
                        //            .ToList();

                        //        return await Task.FromResult(result);
                        //    });

                        //    SolarDayData = result
                        //                   .GroupBy(d => d.ProviderName)
                        //                   .ToDictionary(sid => sid.Key,
                        //                   sid => sid.Select(g => new SolarDisplayDayData
                        //                   {
                        //                       ProviderName = g.ProviderName,
                        //                       Time = g.Time,
                        //                       Power = g.Power
                        //                   }).ToList());

                        //    DetermineTickDistance(SolarDayData);
                        //}

                        break;


                    case PeriodsEnums.Week:
                        {
                            var result = await _solarEdgeDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => DateChosen.StartOfWeek() <= sed.Time && DateChosen!.EndOfWeek().AddDays(1).AddSeconds(-1) >= sed.Time)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            SolarWeekData = new Dictionary<string, Dictionary<DateTime, double>>();

                            ProviderNames = result.Select(sid => sid.ProviderName).Distinct().ToList();

                            foreach (var providerName in ProviderNames)
                            {
                                var dateData = new Dictionary<DateTime, double>();

                                SolarWeekData.Add(providerName, dateData);

                                var dates = result.Select(sid => sid.Time.Date).Distinct();

                                foreach (var date in dates)
                                {
                                    dateData.Add(date.Date, result.Where(sid => sid.ProviderName == providerName && sid.Time.Date == date).Sum(sid => sid.Power) / 4000);
                                }
                            }

                            DetermineTickDistance(result);
                        }

                        break;

                    case PeriodsEnums.Month:
                        {
                            var result = await _solarEdgeDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => DateChosen.StartOfMonth() <= sed.Time && DateChosen!.EndOfMonth().AddDays(1).AddSeconds(-1) >= sed.Time)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            SolarMonthData = new Dictionary<string, Dictionary<DateTime, double>>();

                            ProviderNames = result.Select(sid => sid.ProviderName).Distinct().ToList();

                            foreach (var providerName in ProviderNames)
                            {
                                var dateData = new Dictionary<DateTime, double>();

                                SolarMonthData.Add(providerName, dateData);

                                var dates = result.Select(sid => sid.Time.Date).Distinct();

                                foreach (var date in dates)
                                {
                                    var subset = result.Where(sid => sid.ProviderName == providerName && sid.Time.Date == date);

                                    var kWh = subset.Sum(sid => sid.Power) / 4000;

                                    dateData.Add(date.Date, kWh);
                                }
                            }

                            DetermineTickDistance(result);
                        }

                        break;

                    case PeriodsEnums.Year:
                        {
                            var startDate = new DateTime(DateChosen.Year, 1, 1);
                            var endDate = new DateTime(DateChosen.Year, 12, 31, 23, 59, 59);

                            var result = await _solarEdgeDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => startDate <= sed.Time && endDate >= sed.Time)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            SolarYearData = new Dictionary<string, Dictionary<int, double>>();

                            ProviderNames = result.Select(sid => sid.ProviderName).Distinct().ToList();

                            foreach (var providerName in ProviderNames)
                            {
                                var dateData = new Dictionary<int, double>();

                                SolarYearData.Add(providerName, dateData);

                                var dates = result.Select(sid => sid.Time.Month).Distinct();

                                foreach (var month in dates)
                                {
                                    var subset = result.Where(sid => sid.ProviderName == providerName && sid.Time.Month == month);

                                    var kWh = subset.Sum(sid => sid.Power) / 4000;

                                    dateData.Add(month, kWh);
                                }
                            }

                            DetermineTickDistance(result);
                        }

                        break;

                    case PeriodsEnums.All:
                        {
                            var startDate = DateTime.MinValue;
                            var endDate = DateTime.MaxValue;

                            var result = await _solarEdgeDataService!.GetList(async (set) =>
                            {
                                var result = set
                                    .Where(sed => startDate <= sed.Time && endDate >= sed.Time)
                                    .ToList();

                                return await Task.FromResult(result);
                            });

                            SolarAllData = new Dictionary<string, Dictionary<int, double>>();

                            ProviderNames = result.Select(sid => sid.ProviderName).Distinct().ToList();

                            foreach (var providerName in ProviderNames)
                            {
                                var dateData = new Dictionary<int, double>();

                                SolarAllData.Add(providerName, dateData);

                                var dates = result.Select(sid => sid.Time.Year).Distinct();

                                foreach (var year in dates)
                                {
                                    var subset = result.Where(sid => sid.ProviderName == providerName && sid.Time.Year == year);

                                    var kWh = subset.Sum(sid => sid.Power) / 4000;

                                    dateData.Add(year, kWh);
                                }
                            }

                            DetermineTickDistance(result);
                        }

                        break;

                    default:
                        throw new InvalidOperationException($"Wrong period type {DateSelectionChosen!.PeriodChosen}");
                }

                ProviderNames.Insert(0, "All");

                SolarPower = await GetSolarPower(DateSelectionChosen!.DateChosen!.Value);

                FillFormatter();

                StateHasChanged();

                await SolarPowerChart!.Reload();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void DetermineTickDistance(List<SolarInverterData>? solarInverterData)
        {
            TickDistance = SolarPowerChartWidth;

            if (solarInverterData != null && solarInverterData.Count > 0)
            {
                var start = solarInverterData.Min(list => list.Time);
                var end = solarInverterData.Max(list => list.Time).AddDays(1).AddSeconds(-1);

                switch (DateSelectionChosen!.PeriodChosen)
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
                        {
                            var months = (end - start).Days / 31;

                            TickDistance = SolarPowerChartWidth / (months == 0 ? 12 : months);

                            break;
                        }

                    case PeriodsEnums.All:
                        {
                            var years = (end - start).Days / 365 + 1;

                            TickDistance = SolarPowerChartWidth / (years == 0 ? 1 : years);

                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Invalid period: {DateSelectionChosen!.PeriodChosen}");
                }
            }
        }

        private async Task<double> GetSolarPower(DateTime date)
        {
            DateTime start;
            DateTime end;

            switch (DateSelectionChosen!.PeriodChosen)
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
                    throw new InvalidOperationException($"Wrong period chosen {DateSelectionChosen!.PeriodChosen}");
            }

            return await _solarService!.GetRealizedSolarPower(start, end);
        }

        public class SolarDisplayDayData
        {
            public string ProviderName { get; set; } = string.Empty;
            public DateTime Time { get; set; }
            public double Power { get; set; }
        }

        public class SolarDisplayWeekData
        {
            public string ProviderName { get; set; } = string.Empty;
            public DayOfWeek Position { get; set; }
            public double Power { get; set; }
        }

        public class SolarDisplayMonthData
        {
            public string ProviderName { get; set; } = string.Empty;
            public DateTime Time { get; set; }
            public double Power { get; set; }
        }

        public class SolarDisplayYearData
        {
            public string ProviderName { get; set; } = string.Empty;
            public DateTime Time { get; set; }
            public double Power { get; set; }
        }

        public class SolarDisplayAllData
        {
            public string ProviderName { get; set; } = string.Empty;
            public DateTime Time { get; set; }
            public double Power { get; set; }
        }
    }
}
