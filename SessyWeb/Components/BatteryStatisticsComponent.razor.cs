using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Battery tab of the Statistics page: energy, round-trip efficiency and full cycles per month.</summary>
    public partial class BatteryStatisticsComponent : BaseComponent
    {
        [Inject]
        private BatteryStatisticsService? BatteryStatisticsService { get; set; }

        [Inject]
        private CycleHistoryService? CycleHistoryService { get; set; }

        private const string HowToRead =
            "How hard the batteries work and how much energy they lose. A full cycle is charging and discharging the " +
            "whole capacity once; two half discharges count as one cycle. Only quarters marked as reliable are counted.";

        private const string EnergyInfo =
            "Bars: energy that went into the batteries and came out, per month. Line: round-trip efficiency, the share " +
            "of the energy put in that came out again, corrected for what was still stored at the start and end of the " +
            "month. Months with less than 20 kWh charged show no efficiency: too little to measure.";

        private const string CyclesInfo =
            "Full-cycle equivalents per month (discharged energy ÷ capacity in use), since the batteries were bought. " +
            "Measured: the batteries' own readings. Reconstructed: the energy balance, consumption − solar − grid, where " +
            "no battery reading was stored (a few % high). Estimated: the measured average per quarter, where no data " +
            "exists at all. A single battery counts as measured only from its own readings; before those it follows the " +
            "whole bank. Line: all cycles added up. Switch the estimate off to see only what the data supports.";

        private const string ForecastInfo =
            "Per battery: the rated cycles (entered with the battery investment on the Settings page) divided by the " +
            "current pace, and the purchase date plus the rated lifetime in years. Whichever comes first is the expected " +
            "end of life. The pace is the last twelve complete months, so summer and winter both count; with less than a " +
            "year of history it is the average so far, marked with *, which follows the season it covers. Follows the " +
            "estimate switch. Capacity: the measured usable capacity of the last months against the first measured months, " +
            "and the fade per year; the battery counts as worn out at 70 % (the usual warranty threshold). The fade needs " +
            "half a year of measurements before it gives a date.";

        private const string CapacityInfo =
            "Usable capacity per month: the energy a long discharge delivered (at least 40 % of the state of charge) ÷ the " +
            "share of the state of charge it took, median of the month. Measured after the inverter, so below the nominal " +
            "capacity; the trend is what counts. Rough per discharge (one reading per quarter), months with fewer than three " +
            "discharges are left out of the trend. Only meaningful if the battery reports its state of charge against its " +
            "real, aged capacity.";

        private static readonly string CapacityEndTitle = $"{CapacityHealth.EndOfLifePercent:0} % reached";

        private bool _withEstimate = true;
        private BatteryStatisticsResult? _result;
        private CycleHistoryResult? _cycles;

        // Months without a measurable efficiency are left out of the line.
        private List<BatteryMonth> EfficiencyMonths => _result?.Months.Where(m => m.RoundTripPercent.HasValue).ToList() ?? new();

        private IEnumerable<CycleSeries> AllSeries => _cycles == null
            ? Enumerable.Empty<CycleSeries>()
            : new[] { _cycles.Total }.Concat(_cycles.Batteries);

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync(refresh: false);
        }

        private Task RefreshAsync() => LoadAsync(refresh: true);

        private async Task LoadAsync(bool refresh)
        {
            if (BatteryStatisticsService == null || CycleHistoryService == null) return;

            await WhileLoadingAsync(async () =>
            {
                // Off the circuit thread and side by side: all battery quarters since the start of recording.
                var resultTask = Task.Run(() => BatteryStatisticsService.GetAsync());
                var cyclesTask = Task.Run(() => CycleHistoryService.GetAsync(refresh));

                await Task.WhenAll(resultTask, cyclesTask).ConfigureAwait(true);

                _result = resultTask.Result;
                _cycles = cyclesTask.Result;
            });
        }

        private double TotalCycles => _cycles?.Total.TotalCycles(_withEstimate) ?? _result!.TotalCycles;

        private string CyclesText() => _cycles?.Total.Start is DateTime start
            ? string.Format(GetFormatProvider(),
                "{0:0} full cycles since {1:dd-MM-yyyy}{2}; measured pace {3:0} per year, on {4:0.0} kWh capacity.",
                TotalCycles, start, _withEstimate ? " (incl. estimate)" : " (data only, without estimate)",
                _result!.CyclesPerYear, _result.CapacityKWh)
            : string.Format(GetFormatProvider(),
                "{0:0} full cycles since {1:dd-MM-yyyy} ({2:0} per year) on {3:0.0} kWh capacity.",
                _result!.TotalCycles, _result.FirstDay, _result.CyclesPerYear, _result.CapacityKWh);

        private string SeriesText(CycleSeries series) => string.Format(GetFormatProvider(),
            "{0}: {1:0} full cycles since {2:dd-MM-yyyy} on {3:0.0} kWh{4}.",
            series.Name, series.TotalCycles(_withEstimate), series.Start, series.CapacityKWh,
            _withEstimate ? string.Format(GetFormatProvider(), ", of which {0:0} estimated", series.Months.Sum(m => m.EstimatedCycles)) : string.Empty);

        private DateTime Now => _timeZoneService?.Now ?? DateTime.UtcNow;

        private List<CycleLifetime> Lifetimes => _cycles?.Lifetimes(_withEstimate, Now) ?? new();

        private string LifetimeText()
        {
            if (_cycles == null)
                return string.Empty;

            var bank = _cycles.Total.Lifetime(_withEstimate, Now);

            if (bank.RatedCycles <= 0 && bank.CalendarEnd == null)
                return "Enter the expected cycles and lifetime with the battery investments on the Settings page to see a lifetime forecast.";

            var parts = new List<string>();

            if (bank.UsedPercent is double pct)
                parts.Add(string.Format(GetFormatProvider(), "{0:0} of {1} rated cycles used ({2:0.0} %)", bank.UsedCycles, bank.RatedCycles, pct));

            if (Lifetimes.Where(l => l.End.HasValue).OrderBy(l => l.End).FirstOrDefault() is CycleLifetime first)
                parts.Add(string.Format(GetFormatProvider(), "first expected end of life: {0} in {1:MMMM yyyy} ({2})",
                    first.Name, first.End, first.LimitedBy.ToString().ToLowerInvariant()));

            return string.Join("; ", parts) + ".";
        }

        private string UsedText(CycleLifetime l) => l.UsedPercent is double pct
            ? string.Format(GetFormatProvider(), "{0:0} / {1} ({2:0} %)", l.UsedCycles, l.RatedCycles, pct)
            : string.Format(GetFormatProvider(), "{0:0}", l.UsedCycles);

        private string PaceText(CycleLifetime l) => l.CyclesPerYear > 0.0
            ? string.Format(GetFormatProvider(), "{0:0}{1}", l.CyclesPerYear, l.PaceOverFullYear ? string.Empty : " *")
            : "—";

        private string CapacityText(CycleLifetime l) => l.Health?.CurrentPercent is double pct
            ? string.Format(GetFormatProvider(), "{0:0} %{1}", pct,
                l.Health.FadePercentPerYear is double fade ? string.Format(GetFormatProvider(), " ({0:+0.0;−0.0} %/yr)", fade) : string.Empty)
            : "—";

        private string DateText(DateTime? date) => date.HasValue ? date.Value.ToString("MMM yyyy", GetFormatProvider()) : "—";

        private string EndText(CycleLifetime l) => l.End.HasValue
            ? string.Format(GetFormatProvider(), "{0:MMM yyyy} ({1:0.0} yr)", l.End, (l.End.Value - Now).TotalDays / 365.25)
            : "—";
    }
}
