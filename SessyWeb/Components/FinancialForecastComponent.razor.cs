using Microsoft.AspNetCore.Components;
using SessyController.Services;

namespace SessyWeb.Components
{
    /// <summary>
    /// Next year's electricity bill: last 12 months as measured next to the planner's replay of that
    /// year at next year's tariffs (from the background service).
    /// </summary>
    public partial class FinancialForecastComponent : BaseComponent
    {
        [Inject]
        private FinancialForecastService? FinancialForecastService { get; set; }

        [Inject]
        private FinancialForecastSimulationService? SimulationService { get; set; }

        private sealed record Line(string Label, double LastYear, double? Replay, bool IsEuro, bool Bold = false)
        {
            public double? Difference => Replay - LastYear;
        }

        private FinancialForecast? _lastYear;
        private FinancialForecast? _nextYear; // only for the tariff year and netting in the intro
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

            await WhileLoadingAsync(async () =>
            {
                // Off the circuit thread: a year of meter readings.
                var forecast = await Task.Run(() => FinancialForecastService.GetForecastAsync()).ConfigureAwait(true);

                _lastYear = forecast?.LastYear;
                _nextYear = forecast?.NextYear;
                _replay = SimulationService?.Result;
                _replayProgress = SimulationService?.ProgressDays;
                _lines = _lastYear == null ? new() : BuildLines(_lastYear, _replay?.Forecast);
            });
        }

        private List<Line> BuildLines(FinancialForecast last, FinancialForecast? replay) =>
        [
            new(T("Grid import (kWh)"), last.ImportKWh, replay?.ImportKWh, false),
            new(T("Grid export (kWh)"), last.ExportKWh, replay?.ExportKWh, false),
            new(T("Net (kWh)"), last.NetKWh, replay?.NetKWh, false),
            new(T("Electricity bought"), last.ImportCostEur, replay?.ImportCostEur, true),
            new(T("Feed-in revenue"), -last.ExportRevenueEur, -replay?.ExportRevenueEur, true),
            new(T("Energy tax"), last.EnergyTaxEur, replay?.EnergyTaxEur, true),
            new(T("Grid operator and transport"), last.FixedCostsEur, replay?.FixedCostsEur, true),
            new(T("Energy tax reduction"), -last.TaxReductionEur, -replay?.TaxReductionEur, true),
            new(T("Total per year"), last.TotalEur, replay?.TotalEur, true, Bold: true),
            new(T("Per month"), last.TotalEur / 12.0, replay?.TotalEur / 12.0, true)
        ];

        private string Format(double? value, bool isEuro)
        {
            if (!value.HasValue) return "–";
            return isEuro ? value.Value.ToString("C", GetFormatProvider()) : value.Value.ToString("N0", GetFormatProvider());
        }

        private string FormatDifference(Line line)
            => (line.Difference > 0 ? "+" : "") + Format(line.Difference, line.IsEuro); // "–" while no replay

        private string ReplayHeader => _replay != null
            ? $"{_replay.Forecast.Label} ({_replay.CalculatedAt.ToString("dd-MM HH:mm", GetFormatProvider())})"
            : _replayProgress.HasValue ? T("Planner replay: calculating ({0}/365 days)", _replayProgress) : T("Planner replay: pending");
    }
}
