using Microsoft.AspNetCore.Components;
using Radzen.Blazor.Rendering;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;

namespace SessyWeb.Pages
{
    public partial class SolarPowerPage : PageBase
    {
        [Inject]
        SolarEdgeDataService? _solarEdgeDataService { get; set; }

        [Inject]
        TimeZoneService? _timeZoneService { get; set; }

        public List<SolarEdgeData> SolarEdgeData { get; set; } = new();

        public DateTime? DateChosen { get; set; }

        private string GraphStyle { get; set; } = "min-width: 500px;";

        public enum PeriodsEnums
        {
            Day,
            Week,
            Month,
            Year,
            All
        };

        List<PeriodsEnums> Periods = new List<PeriodsEnums>
        {
            PeriodsEnums.Day, PeriodsEnums.Week, PeriodsEnums.Month, PeriodsEnums.Year, PeriodsEnums.All };

        public PeriodsEnums PeriodChosen { get; set; } = PeriodsEnums.Day;

        public void DateChanged(DateTime? date)
        {
            SelectionChanged();
        }

        public void PeriodChanged(object obj)
        {
            SelectionChanged();
        }

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
                                .Where(sed => date.StartOfWeek() <= sed.Time && date.EndOfWeek() >= sed.Time)
                                .ToList();

                        case PeriodsEnums.Month:
                            return set
                                .Where(sed => date.StartOfMonth() <= sed.Time && date.EndOfMonth() >= sed.Time)
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
        }
    }
}
