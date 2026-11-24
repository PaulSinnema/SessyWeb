using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyCommon;

namespace SessyWeb.Components
{
    /// <summary>
    /// Battery power vs state of charge, now and at the coolest and warmest measured temperature:
    /// the limits the planner uses,
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
            public double ChargeColdW { get; init; }
            public double ChargeWarmW { get; init; }
            public double DischargeW { get; init; }
            public double DischargeColdW { get; init; }
            public double DischargeWarmW { get; init; }
            public double ChargeNameplateW { get; init; }
            public double DischargeNameplateW { get; init; }
        }

        private List<PowerPoint> _points = new();
        private double _temperatureC;
        private string _chargeSource = string.Empty;
        private string _dischargeSource = string.Empty;
        private string _chargeTemperatureText = string.Empty;
        private string _dischargeTemperatureText = string.Empty;
        private bool _chargeHasSlope;
        private bool _dischargeHasSlope;
        private double _chargeColdC, _chargeWarmC, _dischargeColdC, _dischargeWarmC;

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

            await WhileLoadingAsync(async () =>
            {
                StateHasChanged();

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
                double chargeCapKWh = PowerLimits.BaseChargeKW(maxChargeKW, taper, fallback) * Units.QuarterHours;
                double dischargeCapKWh = PowerLimits.BaseDischargeKW(maxDischargeKW, dischargeCapability, fallback) * Units.QuarterHours;

                double ChargeW(double soc, double tempC) => Math.Round(PowerLimits.ChargeKWh(chargeCapKWh, soc, Units.QuarterHours,
                    maxChargeKW, chargeCapability, taper, floor, efficiency, tempC, tempC) / Units.QuarterHours * 1000.0);

                double DischargeW(double soc, double tempC) => Math.Round(PowerLimits.DischargeKWh(dischargeCapKWh, soc,
                    Units.QuarterHours, dischargeCapability, tempC) / Units.QuarterHours * 1000.0);

                _temperatureC = WeatherService.GetTemperature(_timeZoneService.Now) ?? ChargeTaper.RefTemperatureC;

                // Thin lines at the coolest and warmest temperature each side was measured at.
                _chargeHasSlope = chargeCapability.HasTemperatureSlope;
                _dischargeHasSlope = dischargeCapability.HasTemperatureSlope;
                _chargeColdC = _chargeHasSlope ? chargeCapability.MinTemperatureC : _temperatureC;
                _chargeWarmC = _chargeHasSlope ? chargeCapability.MaxTemperatureC : _temperatureC;
                _dischargeColdC = _dischargeHasSlope ? dischargeCapability.MinTemperatureC : _temperatureC;
                _dischargeWarmC = _dischargeHasSlope ? dischargeCapability.MaxTemperatureC : _temperatureC;

                _points = Enumerable.Range(0, 21)
                    .Select(i => i * 5)
                    .Select(pct => new PowerPoint
                    {
                        SocPct = pct,
                        ChargeW = ChargeW(pct / 100.0, _temperatureC),
                        ChargeColdW = ChargeW(pct / 100.0, _chargeColdC),
                        ChargeWarmW = ChargeW(pct / 100.0, _chargeWarmC),
                        DischargeW = DischargeW(pct / 100.0, _temperatureC),
                        DischargeColdW = DischargeW(pct / 100.0, _dischargeColdC),
                        DischargeWarmW = DischargeW(pct / 100.0, _dischargeWarmC),
                        ChargeNameplateW = chargeNameplateW,
                        DischargeNameplateW = dischargeNameplateW
                    })
                    .ToList();

                _chargeSource = chargeCapability.Samples > 0 ? T("measured per SOC band, taper/floor where a band has no data")
                    : taper.Samples > 0 ? T("measured taper with floor")
                    : T("not measured yet, {0:F0}% of nameplate", fallback * 100.0);
                _dischargeSource = dischargeCapability.Samples > 0 ? T("measured plateau and knee")
                    : T("not measured yet, {0:F0}% of nameplate", fallback * 100.0);

                _chargeTemperatureText = chargeCapability.HasTemperatureSlope
                    ? SlopeText(chargeCapability.TemperatureSlopeWPerC, chargeCapability.MinTemperatureC, chargeCapability.MaxTemperatureC)
                    : T("no temperature effect measured yet");
                _dischargeTemperatureText = dischargeCapability.HasTemperatureSlope
                    ? SlopeText(dischargeCapability.TemperatureSlopeWPerC, dischargeCapability.MinTemperatureC, dischargeCapability.MaxTemperatureC)
                    : T("no temperature effect measured yet");
            });

            StateHasChanged();
        }

        private string SlopeText(double slopeWPerC, double minC, double maxC)
            => T("{0:+0;-0} W per °C warmer, measured between {1:F1} and {2:F1} °C", slopeWPerC, minC, maxC);
    }
}
