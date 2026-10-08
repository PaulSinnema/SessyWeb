using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;

namespace SessyWeb.Components
{
    public partial class ThrottleReportComponent : BaseComponent
    {
        [Inject]
        private ThrottleAnalysisService? ThrottleAnalysisService { get; set; }

        [Inject]
        private BatteryEfficiencyService? BatteryEfficiencyService { get; set; }

        private sealed class ChartPoint
        {
            public string Band { get; init; } = string.Empty;
            public double Pct { get; init; }
        }

        // 0 = whole history.
        private int _days = 30;
        private bool _loading = true;
        private ThrottleReport _report = ThrottleReport.Empty;
        private double? _roundTrip;
        private CycleLoss? _loss;
        private List<ChartPoint> _chargePoints = new();
        private List<ChartPoint> _dischargePoints = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task OnRangeChangedAsync(int days)
        {
            _days = days;
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (ThrottleAnalysisService == null || BatteryEfficiencyService == null || batteryContainer == null) return;

            _loading = true;
            StateHasChanged();

            try
            {
                int? days = _days > 0 ? _days : null;
                var now = _timeZoneService!.Now;
                var from = days.HasValue ? now.AddDays(-days.Value) : DateTime.MinValue;
                double chargeW = batteryContainer.GetChargingCapacityInWattsPerHour();
                double dischargeW = batteryContainer.GetDischargingCapacityInWattsPerHour();

                // Off the circuit thread: the whole history is a lot of rows.
                _report = await Task.Run(() => ThrottleAnalysisService.GetThrottleReportAsync(days, chargeW, dischargeW))
                    .ConfigureAwait(true);
                _roundTrip = await Task.Run(() => BatteryEfficiencyService.MeasureRoundTripAsync(from, now))
                    .ConfigureAwait(true);

                _loss = _roundTrip.HasValue && _report.HasData ? _report.LossPerCycle(_roundTrip.Value) : null;

                // Zero, not null, for an empty band: a null value crashes Radzen's tooltip.
                _chargePoints = _report.Bands
                    .Select(b => new ChartPoint { Band = b.Label, Pct = Math.Round((b.ChargeShare ?? 0.0) * 100.0, 1) })
                    .ToList();
                _dischargePoints = _report.Bands
                    .Select(b => new ChartPoint { Band = b.Label, Pct = Math.Round((b.DischargeShare ?? 0.0) * 100.0, 1) })
                    .ToList();
            }
            finally
            {
                _loading = false;
                StateHasChanged();
            }
        }

        private static string Watts(double? w) => w.HasValue ? $"{w.Value:F0} W" : "–";

        private static string Pct(double? share, int decimals = 0)
            => share.HasValue ? (share.Value * 100.0).ToString($"F{decimals}") + "%" : "–";

        private static string Hours(double? h) => h.HasValue ? $"{h.Value:F1} h" : "–";

        private static string KWh(double? kWh) => kWh.HasValue ? $"{kWh.Value:F1} kWh" : "–";
    }
}
