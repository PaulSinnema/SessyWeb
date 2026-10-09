using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;

namespace SessyWeb.Components
{
    /// <summary>
    /// Battery power vs state of charge and vs outside temperature: the limits the planner uses,
    /// computed by the same PowerLimits functions.
    /// </summary>
    public partial class ThrottleChartComponent : BaseComponent
    {
        [Inject]
        private ThrottleAnalysisService? ThrottleAnalysisService { get; set; }

        [Inject]
        private BatteryEfficiencyService? BatteryEfficiencyService { get; set; }

        [Inject]
        private SettingsService? SettingsService { get; set; }

        [Inject]
        private WeatherService? WeatherService { get; set; }

        private sealed class PowerPoint
        {
            public int SocPct { get; init; }
            public double ChargeW { get; init; }
            public double DischargeW { get; init; }
            public double ChargeNameplateW { get; init; }
            public double DischargeNameplateW { get; init; }
        }

        private sealed class TemperaturePoint
        {
            public double TemperatureC { get; init; }
            public double ChargeW { get; init; }
            public double DischargeW { get; init; }
        }

        private const double QuarterHours = 0.25;

        // SOC at which the temperature chart is read: above the discharge knee, inside the charge data.
        private const double TemperatureChartSoc = 0.5;

        private List<PowerPoint> _points = new();
        private List<TemperaturePoint> _temperaturePoints = new();
        private bool _loading = true;
        private double _temperatureC;
        private string _chargeSource = string.Empty;
        private string _dischargeSource = string.Empty;
        private string _chargeTemperatureText = string.Empty;
        private string _dischargeTemperatureText = string.Empty;

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
            if (ThrottleAnalysisService == null || BatteryEfficiencyService == null || SettingsService == null
                || WeatherService == null || batteryContainer == null || _timeZoneService == null) return;

            _loading = true;
            StateHasChanged();

            try
            {
                double chargeNameplateW = batteryContainer.GetChargingCapacityInWattsPerHour();
                double dischargeNameplateW = batteryContainer.GetDischargingCapacityInWattsPerHour();
                double maxChargeKW = chargeNameplateW / 1000.0;
                double maxDischargeKW = dischargeNameplateW / 1000.0;

                // Same models as StrategyMilpService hands to the planner.
                var taper = await ThrottleAnalysisService.GetChargeTaperAsync();
                var dischargeCapability = await ThrottleAnalysisService.GetDischargeCapabilityAsync(dischargeNameplateW);
                var floor = await ThrottleAnalysisService.GetChargeCapabilityFloorAsync(chargeNameplateW);
                var chargeCapability = await ThrottleAnalysisService.GetChargeCapabilityAsync(chargeNameplateW);
                var efficiency = await BatteryEfficiencyService.GetEfficiencyCurveAsync();

                double fallback = PowerLimits.FallbackRatio(SettingsService.Current.ThrottleFallbackPct);
                double chargeCapKWh = PowerLimits.BaseChargeKW(maxChargeKW, taper, fallback) * QuarterHours;
                double dischargeCapKWh = PowerLimits.BaseDischargeKW(maxDischargeKW, dischargeCapability, fallback) * QuarterHours;

                double ChargeW(double soc, double tempC) => Math.Round(PowerLimits.ChargeKWh(chargeCapKWh, soc, QuarterHours,
                    maxChargeKW, chargeCapability, taper, floor, efficiency, tempC, tempC) / QuarterHours * 1000.0);

                double DischargeW(double soc, double tempC) => Math.Round(PowerLimits.DischargeKWh(dischargeCapKWh, soc,
                    QuarterHours, dischargeCapability, tempC) / QuarterHours * 1000.0);

                _temperatureC = WeatherService.GetTemperature(_timeZoneService.Now) ?? ChargeTaper.RefTemperatureC;

                _points = Enumerable.Range(0, 21)
                    .Select(i => i * 5)
                    .Select(pct => new PowerPoint
                    {
                        SocPct = pct,
                        ChargeW = ChargeW(pct / 100.0, _temperatureC),
                        DischargeW = DischargeW(pct / 100.0, _temperatureC),
                        ChargeNameplateW = chargeNameplateW,
                        DischargeNameplateW = dischargeNameplateW
                    })
                    .ToList();

                _temperaturePoints = Enumerable.Range(0, 15)
                    .Select(i => i * 2.5)
                    .Select(t => new TemperaturePoint
                    {
                        TemperatureC = t,
                        ChargeW = ChargeW(TemperatureChartSoc, t),
                        DischargeW = DischargeW(TemperatureChartSoc, t)
                    })
                    .ToList();

                _chargeSource = chargeCapability.Samples > 0 ? "measured per SOC band, taper/floor where a band has no data"
                    : taper.Samples > 0 ? "measured taper with floor"
                    : $"not measured yet, {fallback * 100.0:F0}% of nameplate";
                _dischargeSource = dischargeCapability.Samples > 0 ? "measured plateau and knee"
                    : $"not measured yet, {fallback * 100.0:F0}% of nameplate";

                _chargeTemperatureText = chargeCapability.HasTemperatureSlope
                    ? SlopeText(chargeCapability.TemperatureSlopeWPerC, chargeCapability.MinTemperatureC, chargeCapability.MaxTemperatureC)
                    : "no temperature effect measured yet";
                _dischargeTemperatureText = dischargeCapability.HasTemperatureSlope
                    ? SlopeText(dischargeCapability.TemperatureSlopeWPerC, dischargeCapability.MinTemperatureC, dischargeCapability.MaxTemperatureC)
                    : "no temperature effect measured yet";
            }
            finally
            {
                _loading = false;
                StateHasChanged();
            }
        }

        private static string SlopeText(double slopeWPerC, double minC, double maxC)
            => $"{slopeWPerC:+0;-0} W per °C warmer, measured between {minC:F1} and {maxC:F1} °C";
    }
}
