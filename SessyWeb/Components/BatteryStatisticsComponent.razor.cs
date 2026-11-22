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
                // Off the circuit thread: all battery quarters since the start of recording.
                _result = await Task.Run(() => BatteryStatisticsService.GetAsync()).ConfigureAwait(true);
                _cycles = await Task.Run(() => CycleHistoryService.GetAsync(refresh)).ConfigureAwait(true);
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

        private string LifetimeText()
        {
            if (_result!.ExpectedCycles <= 0)
                return "Enter the expected number of cycles with the battery investment on the Settings page to see how much of the rated life is used.";

            var used = TotalCycles;
            var left = _result.CyclesPerYear > 0.0 ? Math.Max(0.0, _result.ExpectedCycles - used) / _result.CyclesPerYear : (double?)null;

            return string.Format(GetFormatProvider(),
                "{0:0} of {1} rated cycles used ({2:0.0} %){3}.",
                used, _result.ExpectedCycles, used / _result.ExpectedCycles * 100.0,
                left.HasValue ? string.Format(GetFormatProvider(), "; at the measured pace about {0:0} years left", left.Value) : string.Empty);
        }
    }
}
