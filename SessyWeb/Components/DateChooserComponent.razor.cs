using Microsoft.AspNetCore.Components;
using Radzen.Blazor.Rendering;
using SessyCommon.Services;
using System.ComponentModel.DataAnnotations;

namespace SessyWeb.Components
{
    public partial class DateChooserComponent : BaseComponent
    {
        [Inject]
        public TimeZoneService? _TimeZoneService { get; set; }

        [Parameter]
        public EventCallback<DateArgs> SelectionChanged { get; set; }

        [Parameter]
        public DateTime? DateFromChosen { get; set; }

        [Parameter]
        public PeriodsEnums PeriodChosen { get; set; }

        [Parameter]
        public DurationEnums DurationChosen { get; set; }

        [Parameter]
        public DateTime Start { get; set; }

        [Parameter]
        public DateTime End { get; set; }

        [Parameter]
        public bool CustomChooserEnabled { get; set; } = false;

        public bool DatePickerVisible { get; set; } = true;

        public string DateFormat { get; set; } = "dd/MM/yyyy";

        public Boolean ShowDays { get; set; } = true;

        public bool YearDisplay { get; set; } = false;

        public bool CustomDisplay { get; set; } = false;

        public List<string> Years { get; set; } = new();

        public string SelectedYear { get; set; } = string.Empty;

        public bool DurationVisible => PeriodChosen == PeriodsEnums.Custom;

        public enum PeriodsEnums
        {
            Day,
            Week,
            Month,
            Year,
            All,
            Custom
        };

        public enum DurationEnums
        {
            [Display(Name = "Last 7 Days")]
            Last7Days,

            [Display(Name = "Last 30 Days")]
            Last30Days,

            [Display(Name = "Last 90 Days")]
            Last90Days,

            [Display(Name = "Last 180 Days")]
            Last180Days,

            [Display(Name = "Last 365 Days")]   
            Last365Days
        };

        List<PeriodsEnums> Periods = new List<PeriodsEnums>
        {
            PeriodsEnums.Day, PeriodsEnums.Week, PeriodsEnums.Month, PeriodsEnums.Year, PeriodsEnums.All, PeriodsEnums.Custom
        };

        List<DurationEnums> Durations = new List<DurationEnums>
        {
            DurationEnums.Last7Days, DurationEnums.Last30Days, DurationEnums.Last90Days, DurationEnums.Last180Days, DurationEnums.Last365Days
        };

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                if(!CustomChooserEnabled)
                {
                    Periods.Remove(PeriodsEnums.Custom);
                }

                DateFromChosen = _TimeZoneService!.Now.Date;
                PeriodChosen = PeriodsEnums.Day;

                var currentYear = _TimeZoneService!.Now.Year;

                for (int index = 0; index < 50; index++)
                {
                    Years.Insert(index, (currentYear - index).ToString());
                }

                SelectedYear = currentYear.ToString();

                await DateSelectionChanged();
            }
        }

        public async Task TodayClicked()
        {
            var now = _TimeZoneService!.Now;

            DateFromChosen = now.Date;
            SelectedYear = DateFromChosen.Value.Year.ToString();

            await DateSelectionChanged();
        }

        public async Task YearChanged()
        {
            var yearChoosen = Convert.ToInt16(SelectedYear);

            DateFromChosen = new DateTime(yearChoosen, 1, 1);

            await DateSelectionChanged();
        }

        private void SetDatePickerParameters(PeriodsEnums period, DurationEnums duration)
        {
            DatePickerVisible = true;
            YearDisplay = false;
            CustomDisplay = false;

            switch (period)
            {
                case PeriodsEnums.Day:
                    DateFormat = "dd/MM/yyyy";
                    ShowDays = true;
                    break;

                case PeriodsEnums.Week:
                    DateFormat = "dd/MM/yyyy";
                    ShowDays = true;
                    break;

                case PeriodsEnums.Month:
                    DateFormat = "MM/yyyy";
                    ShowDays = false;
                    break;

                case PeriodsEnums.Year:
                    YearDisplay = true;
                    DateFormat = "yyyy";
                    ShowDays = false;
                    break;

                case PeriodsEnums.All:
                    DatePickerVisible = false;
                    break;

                case PeriodsEnums.Custom:
                    DatePickerVisible = true;
                    CustomDisplay = true;
                    break;


                default:
                    break;
            }
        }

        public async Task DateChanged(DateTime? dateIn)
        {
            var date = dateIn.HasValue ? dateIn.Value : _TimeZoneService!.Now;

            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                case PeriodsEnums.Week:
                    DateFromChosen = date;
                    break;

                case PeriodsEnums.Month:
                    DateFromChosen = new DateTime(date.Year, date.Month, 1);
                    break;

                case PeriodsEnums.Year:
                    DateFromChosen = new DateTime(date.Year, 1, 1);
                    break;

                case PeriodsEnums.All:
                    break;

                case PeriodsEnums.Custom:
                    break;

                default:
                    break;
            }

            await DateSelectionChanged();
        }

        public async Task PeriodChanged(object obj)
        {
            var period = (PeriodsEnums)obj;

            var args = new DateArgs(PeriodChosen, DurationChosen, DateFromChosen!.Value);

            await SelectionChanged.InvokeAsync(args);

            SetDatePickerParameters(period, DurationChosen);
        }

        public async Task DurationChanged(object obj)
        {
            var duration = (DurationEnums)obj;

            var args = new DateArgs(PeriodChosen, DurationChosen, DateFromChosen!.Value);

            await SelectionChanged.InvokeAsync(args);

            SetDatePickerParameters(PeriodChosen, duration);
        }

        public class DateArgs
        {
            public DateArgs(PeriodsEnums periodChosen, DurationEnums durationChosen, DateTime dateChosen)
            {
                PeriodChosen = periodChosen;
                DurationChosen = durationChosen;
                DateChosen = dateChosen;

                FillStartAndEndDates();
            }

            public PeriodsEnums PeriodChosen { get; set; }
            public DurationEnums DurationChosen { get; set; }
            public DateTime? DateChosen { get; set; }
            public DateTime? Start { get; set; }
            public DateTime? End { get; set; }

            private void FillStartAndEndDates()
            {
                if (DateChosen != null)
                {
                    switch (PeriodChosen)
                    {
                        case PeriodsEnums.Day:
                            Start = DateChosen.Value.Date;
                            End = DateChosen.Value.Date.AddDays(1).AddSeconds(-1);
                            break;

                        case PeriodsEnums.Week:
                            Start = DateChosen.Value.StartOfWeek();
                            End = DateChosen.Value.EndOfWeek();
                            break;

                        case PeriodsEnums.Month:
                            Start = DateChosen.Value.StartOfMonth();
                            End = DateChosen.Value.EndOfMonth();
                            break;

                        case PeriodsEnums.Year:
                            Start = new DateTime(DateChosen.Value.Year, 1, 1).Date;
                            End = Start.Value.AddYears(1).AddSeconds(-1);
                            break;

                        case PeriodsEnums.All:
                            Start = DateTime.MinValue;
                            End = DateTime.MaxValue;
                            break;

                        case PeriodsEnums.Custom:
                            End = DateChosen.Value.Date.AddDays(1).AddSeconds(-1);

                            switch (DurationChosen)
                            {
                                case DurationEnums.Last7Days:
                                    Start = End.Value.AddDays(-7);    
                                    break;

                                case DurationEnums.Last30Days:
                                    Start = End.Value.AddDays(-30);
                                    break;

                                case DurationEnums.Last90Days:
                                    Start = End.Value.AddDays(-90);
                                    break;

                                case DurationEnums.Last180Days:
                                    Start = End.Value.AddDays(-180);
                                    break;

                                case DurationEnums.Last365Days:
                                    Start = End.Value.AddDays(-365);
                                    break;

                                default:
                                    break;
                            }

                            break;

                        default:
                            throw new InvalidOperationException($"Invalid period {PeriodChosen}");
                    }
                }
            }
        }

        private async Task DateSelectionChanged()
        {
            if (DateFromChosen != null)
            {
                await SelectionChanged.InvokeAsync(new DateArgs(PeriodChosen, DurationChosen, DateFromChosen.Value));
            }
        }
    }
}