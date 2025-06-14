using Microsoft.AspNetCore.Components;
using SessyController.Services;

namespace SessyWeb.Components
{
    public partial class DateChooserComponent : BaseComponent
    {
        [Inject]
        public TimeZoneService? TimeZoneService { get; set; }

        [Parameter]
        public EventCallback<DateTime> DateChosenChanged { get; set; }
        [Parameter]
        public EventCallback<PeriodsEnums> PeriodChosenChanged { get; set; }

        [Parameter]
        public DateTime? DateChosen { get; set; }
        [Parameter]
        public PeriodsEnums PeriodChosen { get; set; }

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

            await PeriodChosenChanged.InvokeAsync(period);

            SetDatePickerParameters(period);
        }

        private async Task DateSelectionChanged()
        {
            if (DateChosen != null)
            {
                await DateChosenChanged.InvokeAsync(DateChosen.Value);
            }
        }
    }
}
