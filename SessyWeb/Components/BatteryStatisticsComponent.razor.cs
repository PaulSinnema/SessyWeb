using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Battery tab of the Statistics page: energy, round-trip efficiency and full cycles per month.</summary>
    public partial class BatteryStatisticsComponent : BaseComponent
    {
        [Inject]
        private BatteryStatisticsService? BatteryStatisticsService { get; set; }

        private const string HowToRead =
            "How hard the batteries work and how much energy they lose. A full cycle is charging and discharging the " +
            "whole capacity once; two half discharges count as one cycle. Only quarters marked as reliable are counted.";

        private const string EnergyInfo =
            "Bars: energy that went into the batteries and came out, per month. Line: round-trip efficiency, the share " +
            "of the energy put in that came out again, corrected for what was still stored at the start and end of the " +
            "month. Months with less than 20 kWh charged show no efficiency: too little to measure.";

        private const string CyclesInfo =
            "Bars: full-cycle equivalents per month (discharged energy ÷ total capacity). Line: all cycles added up since " +
            "the first measurement. Compare it with the number of cycles the batteries are rated for.";

        private bool _loading = true;
        private BatteryStatisticsResult? _result;

        // Months without a measurable efficiency are left out of the line.
        private List<BatteryMonth> EfficiencyMonths => _result?.Months.Where(m => m.RoundTripPercent.HasValue).ToList() ?? new();

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (BatteryStatisticsService == null) return;

            _loading = true;

            try
            {
                // Off the circuit thread: all battery quarters since the start of recording.
                _result = await Task.Run(() => BatteryStatisticsService.GetAsync()).ConfigureAwait(true);
            }
            finally
            {
                _loading = false;
            }
        }

        private string CyclesText() => string.Format(GetFormatProvider(),
            "{0:0} full cycles since {1:dd-MM-yyyy} ({2:0} per year) on {3:0.0} kWh capacity.",
            _result!.TotalCycles, _result.FirstDay, _result.CyclesPerYear, _result.CapacityKWh);

        private string LifetimeText() => _result!.ExpectedCycles <= 0
            ? "Enter the expected number of cycles with the battery investment on the Settings page to see how much of the rated life is used."
            : string.Format(GetFormatProvider(),
                "{0:0} of {1} rated cycles used ({2:0.0} %){3}.",
                _result.TotalCycles, _result.ExpectedCycles, _result.TotalCycles / _result.ExpectedCycles * 100.0,
                _result.YearsLeft.HasValue ? string.Format(GetFormatProvider(), "; at this pace about {0:0} years left", _result.YearsLeft.Value) : string.Empty);
    }
}
