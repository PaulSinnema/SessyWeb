using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Derives the battery's real energy efficiency from measurements.
    ///
    /// This is the *energy* round-trip efficiency (kWh out / kWh in), not to be confused with the
    /// throttle ratio, which is a *power* derate. A battery can deliver only 80% of its rated
    /// power when warm and still return 95% of the stored energy — the two are independent.
    ///
    /// The planner needs the one-way efficiencies. Assuming charging and discharging lose the
    /// same fraction, each one-way efficiency is the square root of the round-trip figure:
    ///
    ///     roundTrip = chargeEff * dischargeEff   →   chargeEff = dischargeEff = sqrt(roundTrip)
    ///
    /// The round-trip figure is SOC-corrected: energy still sitting in the battery at the end of
    /// the window was charged but not yet discharged, and would otherwise depress the ratio.
    ///
    /// When too little has been measured, the configured fallback percentage is used instead of
    /// silently assuming a perfect battery.
    /// </summary>
    public class BatteryEfficiencyService
    {
        private readonly QuarterlyMeasurementDataService _measurementDataService;
        private readonly SettingsService _settingsService;
        private readonly TimeZoneService _timeZoneService;

        /// <summary>Window over which the efficiency is measured.</summary>
        private const int LookbackDays = 31;

        /// <summary>Minimum charged energy (kWh) in the window before the measurement is trusted.</summary>
        private const double MinChargedKWh = 20.0;

        /// <summary>Sanity bounds: anything outside this range points at bad data, not a real battery.</summary>
        private const double MinPlausibleRoundTrip = 0.50;
        private const double MaxPlausibleRoundTrip = 1.00;

        public BatteryEfficiencyService(
            QuarterlyMeasurementDataService measurementDataService,
            SettingsService settingsService,
            TimeZoneService timeZoneService)
        {
            _measurementDataService = measurementDataService;
            _settingsService = settingsService;
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// One-way charge and discharge efficiency, both derived from the measured round-trip.
        /// Falls back to the configured percentage when there is not enough reliable data.
        /// </summary>
        public async Task<(double ChargeEfficiency, double DischargeEfficiency)> GetEfficienciesAsync()
        {
            double roundTrip = await GetRoundTripEfficiencyAsync().ConfigureAwait(false);
            double oneWay = Math.Sqrt(roundTrip);
            return (oneWay, oneWay);
        }

        /// <summary>
        /// Measured round-trip efficiency as a fraction (0..1), or the configured fallback.
        /// </summary>
        public async Task<double> GetRoundTripEfficiencyAsync()
        {
            double fallback = Fallback();

            var now = _timeZoneService.Now;
            var start = now.AddDays(-LookbackDays);

            var measurements = await _measurementDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= start && m.Time <= now && m.IsReliable)
                    .OrderBy(m => m.Time)
                    .ToList())).ConfigureAwait(false);

            if (measurements == null || measurements.Count < 2)
                return fallback;

            double chargedKWh = measurements.Sum(m => m.BatteryChargedKWh);
            double dischargedKWh = measurements.Sum(m => m.BatteryDischargedKWh);

            if (chargedKWh < MinChargedKWh || dischargedKWh <= 0.0)
                return fallback;

            // Correct for energy that is still in the battery at the end of the window.
            double startSocKWh = measurements.First().BatteryStateOfChargeWh / 1000.0;
            double endSocKWh = measurements.Last().BatteryStateOfChargeWh / 1000.0;

            double roundTrip = (dischargedKWh - (endSocKWh - startSocKWh)) / chargedKWh;

            if (roundTrip < MinPlausibleRoundTrip || roundTrip > MaxPlausibleRoundTrip)
                return fallback;

            return roundTrip;
        }

        private double Fallback()
        {
            double pct = _settingsService.Current.RoundTripEfficiencyFallbackPct;
            if (pct <= 0.0 || pct > 100.0) pct = 90.0;
            return pct / 100.0;
        }

        // ── Efficiency versus power ───────────────────────────────────────────

        /// <summary>Minimum samples per side before a curve is fitted rather than assumed flat.</summary>
        private const int MinCurveSamples = 40;

        /// <summary>Ignore quarters below this power — mostly standby, and the SOC steps swamp them.</summary>
        private const double MinSamplePowerW = 200.0;

        /// <summary>How long a fitted curve stays valid.</summary>
        private static readonly TimeSpan CurveCacheTime = TimeSpan.FromHours(6);

        private EfficiencyCurve? _cachedCurve;
        private DateTime _cachedCurveAt = DateTime.MinValue;

        /// <summary>
        /// Fits efficiency = ceiling − overhead / power on the measured quarters, per direction.
        ///
        /// Deliberately counts every quarter, whoever was driving. This compares energy in
        /// against the SOC that resulted from it — physics of the converter, independent of who
        /// issued the setpoint — so quarters driven by Charged or by manual override make the fit
        /// better, not worse. Do not add the control-mode filter that ThrottleAnalysisService
        /// needs: that one divides our planned setpoint by the realized power and is meaningless
        /// unless we sent that setpoint. Different question, different rule.
        ///
        /// Falls back to the flat efficiencies when there is too little data, so a fresh
        /// installation behaves exactly as before. The result is cached: it is a month of history
        /// and it barely moves between quarters.
        /// </summary>
        public async Task<EfficiencyCurve> GetEfficiencyCurveAsync()
        {
            var now = _timeZoneService.Now;

            if (_cachedCurve != null && now - _cachedCurveAt < CurveCacheTime)
                return _cachedCurve;

            var (chargeEfficiency, dischargeEfficiency) = await GetEfficienciesAsync().ConfigureAwait(false);
            var flat = EfficiencyCurve.Flat(chargeEfficiency, dischargeEfficiency);

            var start = now.AddDays(-LookbackDays);

            var measurements = await _measurementDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= start && m.Time <= now && m.IsReliable)
                    .OrderBy(m => m.Time)
                    .ToList())).ConfigureAwait(false);

            var curve = Fit(measurements, flat);

            _cachedCurve = curve;
            _cachedCurveAt = now;

            return curve;
        }

        /// <summary>
        /// Pure fit, so it can be tested without a clock or a database. Consecutive quarters give
        /// the AC energy that went in and the stored energy that arrived; the ratio of the two,
        /// regressed on 1/power, separates the fixed overhead from the conversion ceiling.
        /// </summary>
        internal static EfficiencyCurve Fit(
            IReadOnlyList<QuarterlyMeasurement> measurements, EfficiencyCurve fallback)
        {
            if (measurements == null || measurements.Count < 2) return fallback;

            var charge = new List<(double InversePowerKW, double Efficiency)>();
            var discharge = new List<(double InversePowerKW, double Efficiency)>();

            for (int i = 1; i < measurements.Count; i++)
            {
                var previous = measurements[i - 1];
                var current = measurements[i];

                // Only consecutive quarters: a gap makes the SOC difference meaningless.
                if ((current.Time - previous.Time).TotalMinutes is < 14.5 or > 15.5) continue;

                double powerW = Math.Abs(current.BatteryPowerWatts);
                if (powerW < MinSamplePowerW) continue;

                double acWh = powerW * 0.25;
                double storedWh = current.BatteryStateOfChargeWh - previous.BatteryStateOfChargeWh;
                double powerKW = powerW / 1000.0;

                if (current.BatteryPowerWatts < 0.0)
                {
                    // Charging: stored / AC. Above 1.0 is a measurement artefact, not a battery.
                    double efficiency = storedWh / acWh;
                    if (efficiency is > 0.0 and <= 1.0)
                        charge.Add((1.0 / powerKW, efficiency));
                }
                else
                {
                    // Discharging: delivered / drained, so the drain is what left the store.
                    double drainedWh = -storedWh;
                    if (drainedWh <= 0.0) continue;

                    double efficiency = acWh / drainedWh;
                    if (efficiency is > 0.0 and <= 1.0)
                        discharge.Add((1.0 / powerKW, efficiency));
                }
            }

            var (chargeCeiling, chargeOverhead) = FitSide(charge, fallback.ChargeCeiling);
            var (dischargeCeiling, dischargeOverhead) = FitSide(discharge, fallback.DischargeCeiling);

            return new EfficiencyCurve(
                chargeCeiling, chargeOverhead,
                dischargeCeiling, dischargeOverhead,
                Samples: charge.Count + discharge.Count);
        }

        /// <summary>
        /// Least squares of efficiency against 1/power for one direction. Returns the ceiling and
        /// the fixed overhead in kW.
        ///
        /// The physical shape has efficiency falling as 1/power rises, so a non-negative slope
        /// means the data does not show the effect — too few samples, or all of them at the same
        /// power. The flat value is kept then, which is the behaviour from before this existed.
        /// </summary>
        private static (double Ceiling, double OverheadKW) FitSide(
            List<(double InversePowerKW, double Efficiency)> points, double flatEfficiency)
        {
            if (points.Count < MinCurveSamples) return (flatEfficiency, 0.0);

            double meanX = points.Average(p => p.InversePowerKW);
            double meanY = points.Average(p => p.Efficiency);

            double covariance = points.Sum(p => (p.InversePowerKW - meanX) * (p.Efficiency - meanY));
            double variance = points.Sum(p => (p.InversePowerKW - meanX) * (p.InversePowerKW - meanX));

            if (Math.Abs(variance) < 1e-12) return (flatEfficiency, 0.0);

            double slope = covariance / variance;
            if (slope >= 0.0) return (flatEfficiency, 0.0);

            double overheadKW = -slope;             // reported as a positive fixed loss
            double ceiling = meanY + overheadKW * meanX;

            return (ceiling, overheadKW);
        }
    }
}