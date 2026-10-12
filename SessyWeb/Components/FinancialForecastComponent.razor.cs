using Microsoft.AspNetCore.Components;
using SessyController.Services;

namespace SessyWeb.Components
{
    /// <summary>
    /// Next year's electricity bill: last 12 months as measured, the same usage at next year's
    /// tariffs, and the planner's replay of that year at next year's tariffs (from the background service).
    /// </summary>
    public partial class FinancialForecastComponent : BaseComponent
    {
        [Inject]
        private FinancialForecastService? FinancialForecastService { get; set; }

        [Inject]
        private FinancialForecastSimulationService? SimulationService { get; set; }

        private sealed record Line(string Label, double LastYear, double SameUsage, double? Replay, bool IsEuro, bool Bold = false)
        {
            public double Difference => (Replay ?? SameUsage) - LastYear;
        }

        private bool _loading = true;
        private FinancialForecast? _lastYear;
        private FinancialForecast? _sameUsage;
        private FinancialForecastSimulation? _replay;
        private int? _replayProgress;
        private List<Line> _lines = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (FinancialForecastService == null) return;

            _loading = true;
            StateHasChanged();

            try
            {
                // Off the circuit thread: a year of meter readings.
                var forecast = await Task.Run(() => FinancialForecastService.GetForecastAsync()).ConfigureAwait(true);

                _lastYear = forecast?.LastYear;
                _sameUsage = forecast?.NextYear;
                _replay = SimulationService?.Result;
                _replayProgress = SimulationService?.ProgressDays;
                _lines = _lastYear == null || _sameUsage == null ? new() : BuildLines(_lastYear, _sameUsage, _replay?.Forecast);
            }
            finally
            {
                _loading = false;
                StateHasChanged();
            }
        }

        private static List<Line> BuildLines(FinancialForecast last, FinancialForecast same, FinancialForecast? replay) =>
        [
            new("Grid import (kWh)", last.ImportKWh, same.ImportKWh, replay?.ImportKWh, false),
            new("Grid export (kWh)", last.ExportKWh, same.ExportKWh, replay?.ExportKWh, false),
            new("Net (kWh)", last.NetKWh, same.NetKWh, replay?.NetKWh, false),
            new("Electricity bought", last.ImportCostEur, same.ImportCostEur, replay?.ImportCostEur, true),
            new("Feed-in revenue", -last.ExportRevenueEur, -same.ExportRevenueEur, -replay?.ExportRevenueEur, true),
            new("Energy tax", last.EnergyTaxEur, same.EnergyTaxEur, replay?.EnergyTaxEur, true),
            new("Grid operator and transport", last.FixedCostsEur, same.FixedCostsEur, replay?.FixedCostsEur, true),
            new("Energy tax reduction", -last.TaxReductionEur, -same.TaxReductionEur, -replay?.TaxReductionEur, true),
            new("Total per year", last.TotalEur, same.TotalEur, replay?.TotalEur, true, Bold: true),
            new("Per month", last.TotalEur / 12.0, same.TotalEur / 12.0, replay?.TotalEur / 12.0, true)
        ];

        private string Format(double? value, bool isEuro)
        {
            if (!value.HasValue) return "–";
            return isEuro ? value.Value.ToString("C", GetFormatProvider()) : value.Value.ToString("N0", GetFormatProvider());
        }

        private string FormatDifference(Line line)
            => (line.Difference > 0 ? "+" : "") + Format(line.Difference, line.IsEuro);

        private string ReplayHeader => _replay != null
            ? $"{_replay.Forecast.Label} ({_replay.CalculatedAt.ToString("dd-MM HH:mm", GetFormatProvider())})"
            : _replayProgress.HasValue ? $"Planner replay: calculating ({_replayProgress}/365 days)" : "Planner replay: pending";
    }
}
