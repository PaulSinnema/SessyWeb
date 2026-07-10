using SessyCommon.Services;
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
    }
}