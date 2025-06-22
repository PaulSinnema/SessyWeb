using Microsoft.AspNetCore.Components;
using Radzen.Blazor.Rendering;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;
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

        public List<SolarInverterData> SolarEdgeData { get; set; } = new();

        public DateTime? DateChosen { get; set; }

        public PeriodsEnums PeriodChosen { get; set; }

        public double SolarPower { get; set; }

        private string GraphStyle { get; set; } = "width: 100%; height: 60vh";

        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                DateChosen = _timeZoneService!.Now.Date;
                PeriodChosen = PeriodsEnums.Month;

                SelectionChanged();

                StateHasChanged();
            }

            return base.OnAfterRenderAsync(firstRender);
        }

        public void PeriodChosenChanged(PeriodsEnums period)
        {
            PeriodChosen = period;

            SelectionChanged();
        }

        public void DateChosenChanged(DateTime date)
        {
            DateChosen = date;

            SelectionChanged();
        }

        private void SelectionChanged()
        {
            var date = _timeZoneService!.Now.Date;

            if (DateChosen != null)
            {
                date = DateChosen.Value.Date;
            }
            else
            {
                DateChosen = date;
            }

            SolarEdgeData = _solarEdgeDataService!.GetList((set) =>
            {
                switch (PeriodChosen)
                {
                    case PeriodsEnums.Day:
                        return set
                            .Where(sed => sed.Time.Date == DateChosen)
                            .ToList();

                    case PeriodsEnums.Week:
                        return set
                            .Where(sed => date.StartOfWeek() <= sed.Time && date.EndOfWeek().AddDays(1).AddSeconds(-1) >= sed.Time)
                            .ToList();

                    case PeriodsEnums.Month:
                        return set
                            .Where(sed => date.StartOfMonth() <= sed.Time && date.EndOfMonth().AddDays(1).AddSeconds(-1) >= sed.Time)
                            .ToList();

                    case PeriodsEnums.Year:
                        return set
                            .Where(sed => date.Year == sed.Time.Year)
                            .ToList();

                    case PeriodsEnums.All:
                        return set.ToList();

                    default:
                        throw new InvalidOperationException($"Wrong period type {PeriodChosen}");
                }
            })
                .OrderBy(sed => sed.Time)
                .ToList(); ;

            SolarPower = GetSolarPower(date);

            StateHasChanged();
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
