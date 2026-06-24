using Microsoft.AspNetCore.Components;
using SessyController.Services;

namespace SessyWeb.Components
{
    public partial class ThrottleChartComponent : BaseComponent
    {
        [Inject]
        private ThrottleAnalysisService? ThrottleAnalysisService { get; set; }

        /// <summary>One plotted point per temperature bucket (bucket midpoint on the X axis).</summary>
        private sealed class ThrottlePoint
        {
            public double Temperature { get; init; }
            // Null when the bucket has no samples for that direction, so the series skips it
            // instead of drawing a misleading 1.0 default.
            public double? DischargeRatio { get; init; }
            public double? ChargeRatio { get; init; }
        }

        private List<ThrottlePoint> _points = new();
        private List<ThrottlePoint> _dischargePoints = new();
        private List<ThrottlePoint> _chargePoints = new();
        private bool _loading = true;

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task RefreshAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (ThrottleAnalysisService == null) return;

            _loading = true;
            StateHasChanged();

            var buckets = await ThrottleAnalysisService.GetThrottleBucketsAsync();

            // Plot the bucket midpoint so the line sits in the centre of each range. Only
            // plot a ratio when that direction actually has samples in the bucket.
            _points = buckets
                .Select(b => new ThrottlePoint
                {
                    Temperature = b.TemperatureLow + b.Width / 2.0,
                    DischargeRatio = b.DischargeSamples > 0 ? Math.Round(b.DischargeRatio, 3) : (double?)null,
                    ChargeRatio = b.ChargeSamples > 0 ? Math.Round(b.ChargeRatio, 3) : (double?)null
                })
                .ToList();

            // Separate per-series lists with no null values. Radzen's tooltip reads the
            // value property unconditionally, so a null in the bound data crashes it; giving
            // each series only its own populated points avoids that.
            _dischargePoints = _points.Where(p => p.DischargeRatio.HasValue).ToList();
            _chargePoints = _points.Where(p => p.ChargeRatio.HasValue).ToList();

            _loading = false;
            StateHasChanged();
        }
    }
}