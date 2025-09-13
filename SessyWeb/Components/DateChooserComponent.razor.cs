using Microsoft.AspNetCore.Components;
using Radzen.Blazor.Rendering;
using SessyCommon.Services;

namespace SessyWeb.Components
{
    public partial class DateChooserComponent : BaseComponent
    {
        [Inject]
        public TimeZoneService? TimeZoneService { get; set; }

        [Parameter]
        public EventCallback<DateArgs> SelectionChanged { get; set; }

        [Parameter]
        public DateTime? DateChosen { get; set; }
        [Parameter]
        public PeriodsEnums PeriodChosen { get; set; }

        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public bool DatePickerVisible { get; set; } = true;

        public string DateFormat { get; set; } = "dd/MM/yyyy";

        public Boolean ShowDays { get; set; } = true;

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
            PeriodsEnums.Day, PeriodsEnums.Week, PeriodsEnums.Month, PeriodsEnums.Year, PeriodsEnums.All
        };

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                DateChosen = TimeZoneService!.Now.Date;
                PeriodChosen = PeriodsEnums.Day;

                await DateSelectionChanged();
            }
        }

        private void SetDatePickerParameters(PeriodsEnums period)
        {
            DatePickerVisible = true;

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
                    DateFormat = "yyyy";
                    ShowDays = false;
                    break;

                case PeriodsEnums.All:
                    DatePickerVisible = false;
                    break;

                default:
                    break;
            }
        }

        public async Task DateChanged(DateTime? dateIn)
        {
            var date = dateIn.HasValue ? dateIn.Value : TimeZoneService!.Now;

            switch (PeriodChosen)
            {
                case PeriodsEnums.Day:
                case PeriodsEnums.Week:
                    DateChosen = date;
                    break;

                case PeriodsEnums.Month:
                    DateChosen = new DateTime(date.Year, date.Month, 1);
                    break;

                case PeriodsEnums.Year:
                    DateChosen = new DateTime(date.Year, 1, 1);
                    break;

                case PeriodsEnums.All:
                    break;

                default:
                    break;
            }

            await DateSelectionChanged();
        }

        public async Task PeriodChanged(object obj)
        {
            var period = (PeriodsEnums)obj;

            var args = new DateArgs(PeriodChosen, DateChosen!.Value);

            await SelectionChanged.InvokeAsync(args);

            SetDatePickerParameters(period);
        }

        public class DateArgs
        {
            public DateArgs(PeriodsEnums periodChosen, DateTime dateChosen)
            {
                PeriodChosen = periodChosen;
                DateChosen = dateChosen;

                FillStartAndEndDates();
            }

            public PeriodsEnums PeriodChosen { get; set; }
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

                        default:
                            throw new InvalidOperationException($"Invalid period {PeriodChosen}");
                    }
                }
            }
        }

        private async Task DateSelectionChanged()
        {
            if (DateChosen != null)
            {
                await SelectionChanged.InvokeAsync(new DateArgs(PeriodChosen, DateChosen.Value));
            }
        }
    }
}
