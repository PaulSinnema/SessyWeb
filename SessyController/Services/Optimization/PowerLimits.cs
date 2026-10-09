using SessyController.Services.Items;

namespace SessyController.Services.Optimization
{
    /// <summary>
    /// The one place where the battery's power limits are decided. The planner and the
    /// "Battery power vs state of charge" chart both call this, so the chart shows exactly
    /// what the plan assumes.
    /// </summary>
    public static class PowerLimits
    {
        /// <summary>Share of nameplate assumed when nothing has been measured yet.</summary>
        public static double FallbackRatio(double throttleFallbackPct)
            => throttleFallbackPct > 0.0 ? throttleFallbackPct / 100.0 : 0.80;

        /// <summary>Charge cap before the SOC models: nameplate, or the fallback share without a taper.</summary>
        public static double BaseChargeKW(double maxChargeKW, ChargeTaper taper, double fallbackRatio)
            => taper.Samples > 0 ? maxChargeKW : maxChargeKW * fallbackRatio;

        /// <summary>Discharge cap before the SOC model: nameplate, or the fallback share without a capability.</summary>
        public static double BaseDischargeKW(double maxDischargeKW, DischargeCapability capability, double fallbackRatio)
            => capability.Samples > 0 ? maxDischargeKW : maxDischargeKW * fallbackRatio;

        /// <summary>
        /// AC energy a quarter can charge from this SOC and outside temperature. Measured sustained
        /// power (temperature-corrected) wins where its SOC bin has data; otherwise the taper,
        /// lifted by the measured floor. Never above capKWh.
        /// </summary>
        public static double ChargeKWh(
            double capKWh,
            double socFraction,
            double dtHours,
            double maxChargeKW,
            ChargeCapability capability,
            ChargeTaper taper,
            ChargeCapabilityFloor floor,
            EfficiencyCurve efficiency,
            double temperatureC,
            double mean48hC)
        {
            // Stored as DC (SOC gain), converted back to AC here.
            double measuredDcW = capability.PowerW(socFraction, temperatureC);
            if (measuredDcW > 0.0)
            {
                double dcKWh = measuredDcW / 1000.0 * dtHours;
                double eff = efficiency.ChargeAt(Math.Max(0.0, dcKWh) / dtHours);
                return Math.Min(capKWh, dcKWh / eff);
            }

            double tapered = capKWh;
            if (taper.Samples > 0)
            {
                double ratio = taper.Ratio(socFraction, temperatureC, mean48hC);
                tapered = Math.Min(capKWh, Math.Max(0.0, maxChargeKW) * ratio * dtHours);
            }

            double floorKWh = floor.PowerW(socFraction) / 1000.0 * dtHours;

            // The floor lifts the prediction, never past what this quarter may take anyway.
            return Math.Min(capKWh, Math.Max(tapered, floorKWh));
        }

        /// <summary>AC energy a quarter can deliver from this SOC and outside temperature: plateau with a knee, never above capKWh.</summary>
        public static double DischargeKWh(double capKWh, double socFraction, double dtHours, DischargeCapability capability,
            double temperatureC)
        {
            if (capability.Samples == 0) return capKWh;

            return Math.Min(capKWh, capability.PowerW(socFraction, temperatureC) / 1000.0 * dtHours);
        }
    }
}
