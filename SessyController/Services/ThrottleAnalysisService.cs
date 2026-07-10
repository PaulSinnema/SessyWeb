using SessyCommon.Enums;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Derives how much the battery is throttled at a given outside temperature by comparing
    /// the planned (requested) power with the realized power, grouped into temperature buckets.
    ///
    /// The relation between outside temperature and available power is causal (hot battery room
    /// → lower power), so buckets are keyed on temperature alone, independent of date. A short
    /// look-back window (default one month) means that if the room is later cooled, the high-
    /// throttle samples age out and the ratios recover automatically within that window.
    ///
    /// Sign conventions differ between the two sources and are normalized here:
    ///   PlannedQuarter.PlannedPowerW : discharge negative, charge positive
    ///   QuarterlyMeasurement.BatteryPowerWatts : discharge positive, charge negative
    /// </summary>
    public class ThrottleAnalysisService
    {
        private readonly PlannedQuarterDataService _plannedQuarterDataService;
        private readonly QuarterlyMeasurementDataService _measurementDataService;
        private readonly ConsumptionDataService _consumptionDataService;
        private readonly IBatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;

        // Tuning constants.
        private const int BucketWidthC = 2;       // temperature bucket width in °C
        private const double MinRequestedW = 3000; // only count quarters with a substantial request
        private const double MinSocPct = 10.0;     // ignore near-empty battery (not a throttle)
        private const double MaxSocPct = 90.0;     // ignore near-full battery (not a throttle)
        private const double EmaAlpha = 0.2;       // EMA weight for newest sample
        private const int LookbackDays = 31;       // short horizon so cooling resolves within a month

        public ThrottleAnalysisService(
            PlannedQuarterDataService plannedQuarterDataService,
            QuarterlyMeasurementDataService measurementDataService,
            ConsumptionDataService consumptionDataService,
            IBatteryContainer batteryContainer,
            TimeZoneService timeZoneService)
        {
            _plannedQuarterDataService = plannedQuarterDataService;
            _measurementDataService = measurementDataService;
            _consumptionDataService = consumptionDataService;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// Computes throttle buckets over the look-back window. Buckets are returned ordered by
        /// temperature. Buckets without samples keep the default ratio of 1.0 (no throttle).
        /// </summary>
        public async Task<List<ThrottleBucket>> GetThrottleBucketsAsync()
        {
            var now = _timeZoneService.Now;
            var start = now.AddDays(-LookbackDays);

            var plans = await _plannedQuarterDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(p => p.Time >= start && p.Time <= now)
                    .ToList()));

            var measurements = await _measurementDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= start && m.Time <= now)
                    .ToList()));

            var consumptions = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(c => c.Time >= start && c.Time <= now)
                    .ToList()));

            // Index plan and temperature by quarter for a fast in-memory join.
            // Use the throttle-free target power as the denominator. If the plan was already
            // throttled, PlannedPowerW would hide future throttling (ratio → 1.0); the
            // unthrottled target keeps the ratio measuring the true, absolute throttle.
            var planByTime = plans
                .GroupBy(p => p.Time)
                .ToDictionary(g => g.Key, g =>
                {
                    var p = g.First();
                    return p.PlannedUnthrottledPowerW != 0.0
                        ? p.PlannedUnthrottledPowerW
                        : p.PlannedPowerW;
                });

            var tempByTime = consumptions
                .Where(c => c.Temperature > -50.0) // drop the -999 sentinel
                .GroupBy(c => c.Time)
                .ToDictionary(g => g.Key, g => g.First().Temperature);

            double capacity = _batteryContainer.GetTotalCapacity();
            if (capacity <= 0) capacity = 1; // guard against divide-by-zero

            // Accumulate samples per bucket before smoothing.
            var buckets = new Dictionary<int, ThrottleBucket>();

            foreach (var m in measurements.OrderBy(m => m.Time))
            {
                if (!planByTime.TryGetValue(m.Time, out var requestedW)) continue;
                if (!tempByTime.TryGetValue(m.Time, out var temperature)) continue;

                if (Math.Abs(requestedW) < MinRequestedW) continue;

                double socPct = m.BatteryStateOfChargeWh / capacity * 100.0;
                if (socPct < MinSocPct || socPct > MaxSocPct) continue;

                // Normalize direction. Plan: discharge < 0; measurement: discharge > 0.
                // The battery must have ACTUALLY executed the planned mode. Direction alone
                // is not enough: in ZeroNetHome the battery also delivers positive power (to
                // cover the house), which would otherwise look like a heavily throttled
                // discharge. Only count quarters where the real mode matches the plan.
                bool planDischarging = requestedW < 0;
                if (planDischarging && m.BatteryMode != Modes.Discharging) continue;
                if (!planDischarging && m.BatteryMode != Modes.Charging) continue;

                double ratio = Math.Min(Math.Abs(m.BatteryPowerWatts) / Math.Abs(requestedW), 1.0);

                int low = (int)Math.Floor(temperature / BucketWidthC) * BucketWidthC;
                if (!buckets.TryGetValue(low, out var bucket))
                {
                    bucket = new ThrottleBucket { TemperatureLow = low, Width = BucketWidthC };
                    buckets[low] = bucket;
                }

                if (planDischarging)
                {
                    bucket.DischargeRatio = bucket.DischargeSamples == 0
                        ? ratio
                        : EmaAlpha * ratio + (1 - EmaAlpha) * bucket.DischargeRatio;
                    bucket.DischargeSamples++;
                }
                else
                {
                    bucket.ChargeRatio = bucket.ChargeSamples == 0
                        ? ratio
                        : EmaAlpha * ratio + (1 - EmaAlpha) * bucket.ChargeRatio;
                    bucket.ChargeSamples++;
                }
            }

            return buckets.Values.OrderBy(b => b.TemperatureLow).ToList();
        }

        /// <summary>
        /// Returns the discharge throttle ratio for a given temperature (1.0 when no data).
        /// Intended for the planner to scale the maximum discharge power per forecast quarter.
        /// </summary>
        public double GetDischargeRatio(IReadOnlyList<ThrottleBucket> buckets, double temperature)
            => FindRatio(buckets, temperature, discharge: true);

        /// <summary>
        /// Returns the charge throttle ratio for a given temperature (1.0 when no data).
        /// </summary>
        public double GetChargeRatio(IReadOnlyList<ThrottleBucket> buckets, double temperature)
            => FindRatio(buckets, temperature, discharge: false);

        /// <summary>
        /// Measured charge throttle ratio for a temperature. Returns false when no bucket has
        /// been observed yet, so the caller can fall back to a configured estimate instead of
        /// silently assuming there is no throttling at all.
        /// </summary>
        public bool TryGetChargeRatio(IReadOnlyList<ThrottleBucket> buckets, double temperature, out double ratio)
            => TryFindRatio(buckets, temperature, discharge: false, out ratio);

        /// <summary>
        /// Measured discharge throttle ratio for a temperature. Returns false when no bucket has
        /// been observed yet.
        /// </summary>
        public bool TryGetDischargeRatio(IReadOnlyList<ThrottleBucket> buckets, double temperature, out double ratio)
            => TryFindRatio(buckets, temperature, discharge: true, out ratio);

        private static bool TryFindRatio(IReadOnlyList<ThrottleBucket> buckets, double temperature, bool discharge, out double ratio)
        {
            ratio = 1.0;

            if (buckets == null) return false;

            int low = (int)Math.Floor(temperature / BucketWidthC) * BucketWidthC;
            var bucket = buckets.FirstOrDefault(b => b.TemperatureLow == low);
            if (bucket == null) return false;

            int samples = discharge ? bucket.DischargeSamples : bucket.ChargeSamples;
            if (samples <= 0) return false;

            ratio = discharge ? bucket.DischargeRatio : bucket.ChargeRatio;
            return ratio > 0.0;
        }

        private static double FindRatio(IReadOnlyList<ThrottleBucket> buckets, double temperature, bool discharge)
        {
            int low = (int)Math.Floor(temperature / BucketWidthC) * BucketWidthC;
            var bucket = buckets.FirstOrDefault(b => b.TemperatureLow == low);
            if (bucket == null) return 1.0;
            return discharge ? bucket.DischargeRatio : bucket.ChargeRatio;
        }
    }
}