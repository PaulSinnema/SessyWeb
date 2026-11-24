using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;
using System.Globalization;

namespace SessyWeb.Components
{
    /// <summary>Average household load per weekday and hour, as a coloured grid.</summary>
    public partial class ConsumptionHeatmapComponent : BaseComponent
    {
        [Inject]
        private ConsumptionPatternService? ConsumptionPatternService { get; set; }

        public sealed record Period(int Days, string Text);

        private List<Period> Periods => [new(30, T("Last 30 Days")), new(90, T("Last 90 Days")), new(365, T("Last 365 Days"))];

        private static readonly DayOfWeek[] Days =
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

        private string HowToRead => T("Every square is one hour of one weekday: the average power the house drew then, in kW, over the chosen " +
            "period. The darker the square, the higher the load. You see at a glance when the house uses most, for " +
            "example the evening peak or the washing machine on Saturday morning. Hover a square for the exact value.");

        private int _days = 90;
        private HeatmapResult? _result;
        private Dictionary<(DayOfWeek, int), HeatmapCell> _cells = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (ConsumptionPatternService == null || _timeZoneService == null) return;

            await WhileLoadingAsync(async () =>
            {
                var to = _timeZoneService.Now.Date;
                _result = await Task.Run(() => ConsumptionPatternService.GetHeatmapAsync(to.AddDays(-_days), to)).ConfigureAwait(true);
                _cells = _result.Cells.ToDictionary(c => (c.Day, c.Hour));
            });
        }

        private HeatmapCell? Cell(DayOfWeek day, int hour) => _cells.TryGetValue((day, hour), out var cell) ? cell : null;

        private string DayName(DayOfWeek day) => UiCulture.DateTimeFormat.GetAbbreviatedDayName(day);

        private string CellStyle(HeatmapCell? cell)
        {
            const string common = "text-align: center; padding: 6px 0; border-radius: 3px;";

            if (cell == null || _result == null)
                return common;

            var range = _result.MaxW - _result.MinW;
            var share = range > 0.0 ? (cell.AverageW - _result.MinW) / range : 0.5;

            return string.Create(CultureInfo.InvariantCulture,
                $"{common} background-color: color-mix(in srgb, var(--sessy-consumption) {10 + share * 85:0}%, transparent);");
        }

        private string CellText(HeatmapCell? cell) => cell == null ? string.Empty : (cell.AverageW / 1000.0).ToString("0.0", GetFormatProvider());

        private string CellTitle(DayOfWeek day, int hour, HeatmapCell? cell) => cell == null
            ? T("{0} {1:00}:00 – no data", DayName(day), hour)
            : T("{0} {1:00}:00 – {2:0} W average over {3} quarters", DayName(day), hour, cell.AverageW, cell.Quarters);

        private string RangeText() => T("{0:dd-MM-yyyy} to {1:dd-MM-yyyy}, average kW per hour.",
            _result!.From, _result.To.AddDays(-1));
    }
}
