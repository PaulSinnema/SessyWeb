using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;

namespace SessyWeb.Components
{
    /// <summary>
    /// Battery power vs state of charge: the charge and discharge limits the planner uses,
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
            public double ChargeNameplateW { get; init; }
            public double DischargeNameplateW { get; init; }
        }

        private const double QuarterHours = 0.25;

        private List<PowerPoint> _points = new();
        private bool _loading = true;
        private double _temperatureC;
        private string _chargeSource = string.Empty;
        private string _dischargeSource = string.Empty;
        private bool _hasTemperatureSlope;
        private double _slopeWPerC;
        private double _coldC;
        private double _warmC;

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

                // Charge power depends on temperature: show it now and at the coolest and warmest measured.
                _temperatureC = WeatherService.GetTemperature(_timeZoneService.Now) ?? ChargeTaper.RefTemperatureC;
                _hasTemperatureSlope = chargeCapability.HasTemperatureSlope;
                _slopeWPerC = chargeCapability.TemperatureSlopeWPerC;
                _coldC = _hasTemperatureSlope ? chargeCapability.MinTemperatureC : _temperatureC;
                _warmC = _hasTemperatureSlope ? chargeCapability.MaxTemperatureC : _temperatureC;

                _points = Enumerable.Range(0, 21)
                    .Select(i => i * 5)
                    .Select(pct =>
                    {
                        double soc = pct / 100.0;
                        double ChargeAtW(double tempC) => Math.Round(PowerLimits.ChargeKWh(chargeCapKWh, soc, QuarterHours,
                            maxChargeKW, chargeCapability, taper, floor, efficiency, tempC, tempC) / QuarterHours * 1000.0);
                        double dischargeKWh = PowerLimits.DischargeKWh(dischargeCapKWh, soc, QuarterHours, dischargeCapability);

                        return new PowerPoint
                        {
                            SocPct = pct,
                            ChargeW = ChargeAtW(_temperatureC),
                            ChargeColdW = ChargeAtW(_coldC),
                            ChargeWarmW = ChargeAtW(_warmC),
                            DischargeW = Math.Round(dischargeKWh / QuarterHours * 1000.0),
                            ChargeNameplateW = chargeNameplateW,
                            DischargeNameplateW = dischargeNameplateW
                        };
                    })
                    .ToList();

                _chargeSource = chargeCapability.Samples > 0 ? "measured per SOC band, taper/floor where a band has no data"
                    : taper.Samples > 0 ? "measured taper with floor"
                    : $"not measured yet, {fallback * 100.0:F0}% of nameplate";
                _dischargeSource = dischargeCapability.Samples > 0 ? "measured plateau and knee"
                    : $"not measured yet, {fallback * 100.0:F0}% of nameplate";
            }
            finally
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }
}
