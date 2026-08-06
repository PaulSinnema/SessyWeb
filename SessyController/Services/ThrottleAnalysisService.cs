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
    /// Every ratio divides by PlannedQuarter.PlannedUnthrottledPowerW, never by PlannedPowerW:
    /// the latter is already throttled, so it would measure the plan against itself and report
    /// almost no throttling. Quarters without a recorded unthrottled target are skipped entirely.
    ///
    /// Sign conventions differ between the two sources and are normalized here:
    ///   PlannedQuarter.PlannedUnthrottledPowerW : discharge negative, charge positive
    ///   QuarterlyMeasurement.BatteryPowerWatts : discharge positive, charge negative
    /// </summary>
    public class ThrottleAnalysisService
    {
        private readonly PlannedQuarterDataService _plannedQuarterDataService;
        private readonly ActualQuarterDataService _actualQuarterDataService;
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
        private const int MinSocOnlySamples = 20;  // enough for the SOC-only fallback fit
        private const int Mean48hHours = 48;       // window for the heat build-up term

        // The fit runs on the upper envelope of the samples, not on all of them (see
        // SelectEnvelope), so it needs fewer — and less noisy — points than a mean fit.
        private const int EnvelopeBins = 10;              // SOC bins, 10% wide
        private const double EnvelopeBandRatio = 0.10;    // keep what is within this of the bin's best
        private const int MinEnvelopePerBin = 3;          // ... but never fewer than this
        private const int MinEnvelopeSamples = 30;        // enough envelope points for 3 regressors
        private const double MaxPlausibleRatio = 1.02;    // above this the pair is a measurement error

        // The house terms (C, D) are fitted over a long window, but not an unbounded one: a
        // change to the room — air conditioning, insulation — makes all older samples wrong.
        // Samples are weighted by age with an exponential half-life, so such a change works
        // through in months instead of being averaged away for years, while a full seasonal
        // cycle still carries measurable weight.
        private const int TaperHistoryDays = 730;      // hard cap: two years
        private const double TaperHalfLifeDays = 120;  // a sample this old counts for half

        /// <summary>How long a fitted taper stays valid; the long window barely moves per quarter.</summary>
        private static readonly TimeSpan TaperCacheTime = TimeSpan.FromHours(6);

        private ChargeTaper? _cachedTaper;
        private DateTime _cachedTaperAt = DateTime.MinValue;

        public ThrottleAnalysisService(
            PlannedQuarterDataService plannedQuarterDataService,
            QuarterlyMeasurementDataService measurementDataService,
            ConsumptionDataService consumptionDataService,
            ActualQuarterDataService actualQuarterDataService,
            IBatteryContainer batteryContainer,
            TimeZoneService timeZoneService)
        {
            _plannedQuarterDataService = plannedQuarterDataService;
            _measurementDataService = measurementDataService;
            _consumptionDataService = consumptionDataService;
            _actualQuarterDataService = actualQuarterDataService;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// Quarters that someone else drove, and that therefore say nothing about our throttle.
        ///
        /// Every ratio here divides a planned setpoint by the realized power, which only means
        /// something when we issued that setpoint. Under Charged the plan is still written but
        /// never executed, so the pair becomes "Charged's power over our unexecuted request" —
        /// and because the envelope fit keeps the *highest* ratio per SOC bin, that is not noise
        /// that averages out but a bias in one direction. Manual override is excluded for the
        /// same reason: it commands full power on clock hours, not the planned setpoint.
        ///
        /// Selecting what to DROP rather than what to keep is deliberate: a quarter with no
        /// ActualQuarter row (before v1.0.51, or a missed cycle) is then kept automatically, so
        /// the existing history stays in the fit.
        /// </summary>
        private async Task<HashSet<DateTime>> LoadForeignQuartersAsync(DateTime from, DateTime to)
        {
            var actuals = await _actualQuarterDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(a => a.Time >= from && a.Time <= to)
                    .ToList()));

            return actuals
                .Where(a => IsForeign(a.ControlMode))
                .Select(a => a.Time)
                .ToHashSet();
        }

        /// <summary>
        /// True when the quarter was driven by someone other than our own planner, and therefore
        /// says nothing about our throttle. Empty means the row predates v1.0.51 — those count as
        /// ours, so the existing history stays in the fit.
        /// </summary>
        internal static bool IsForeign(string? controlMode)
            => !string.IsNullOrEmpty(controlMode)
               && controlMode != nameof(ControlMode.SessyWeb);

        /// <summary>
        /// Computes throttle buckets over the look-back window. Buckets are returned ordered by
        /// temperature. Buckets without samples keep the default ratio of 1.0 (no throttle).
        /// </summary>
        public async Task<List<ThrottleBucket>> GetThrottleBucketsAsync()
        {
            var now = _timeZoneService.Now;
            var start = now.AddDays(-LookbackDays);

            // Only quarters with a recorded throttle-free target count. Falling back to
            // PlannedPowerW would divide by an already-throttled number, so every such sample
            // reports a ratio near 1.0 and drags the measured throttle towards "none".
            var plans = await _plannedQuarterDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(p => p.Time >= start && p.Time <= now && p.PlannedUnthrottledPowerW != 0.0)
                    .ToList()));

            var measurements = await _measurementDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= start && m.Time <= now)
                    .ToList()));

            var consumptions = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(c => c.Time >= start && c.Time <= now)
                    .ToList()));

            var foreignQuarters = await LoadForeignQuartersAsync(start, now).ConfigureAwait(false);

            // Index plan and temperature by quarter for a fast in-memory join.
            var planByTime = plans
                .GroupBy(p => p.Time)
                .ToDictionary(g => g.Key, g => g.First().PlannedUnthrottledPowerW);

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
                if (foreignQuarters.Contains(m.Time)) continue;
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
        /// Fits the charge taper:
        ///
        ///     ratio = A - B * soc - C * (temp - ref) - D * (t48 - temp)
        ///
        /// The terms live on different timescales, so they are fitted over different windows:
        ///
        ///   C, D  — how the building stores heat. A property of the house, not of the battery:
        ///           it only changes when the room is insulated or cooled. Fitted over ALL
        ///           available history, so the picture keeps sharpening over the years and
        ///           eventually covers winter as well as summer.
        ///   A, B  — the CC/CV taper of the battery itself, which does drift (degradation,
        ///           firmware). Fitted over the recent window only, with C and D held fixed so
        ///           a hot spell cannot masquerade as battery wear.
        ///
        /// Deliberately does NOT apply the MinSocPct/MaxSocPct window used for the temperature
        /// buckets — the taper is strongest exactly at the high SOC that window excludes.
        ///
        /// Falls back in steps: no long-window fit → fit everything on the recent window; too
        /// few samples or a singular system → the SOC-only fit; no fall-off with SOC →
        /// ChargeTaper.None, which makes the planner behave as before.
        ///
        /// The result is cached: the long window grows without bound and the fit barely moves
        /// from one quarter to the next.
        /// </summary>
        public async Task<ChargeTaper> GetChargeTaperAsync()
        {
            var now = _timeZoneService.Now;

            if (_cachedTaper != null && now - _cachedTaperAt < TaperCacheTime)
                return _cachedTaper;

            var taper = await FitChargeTaperAsync(now).ConfigureAwait(false);

            _cachedTaper = taper;
            _cachedTaperAt = now;

            return taper;
        }

        private async Task<ChargeTaper> FitChargeTaperAsync(DateTime now)
        {
            double capacity = _batteryContainer.GetTotalCapacity();
            if (capacity <= 0) return ChargeTaper.None;

            var all = await CollectSamplesAsync(now, capacity).ConfigureAwait(false);
            if (all.Count == 0) return ChargeTaper.None;

            // Only the top of each SOC bin says anything about the hardware limit — see
            // SelectEnvelope. Everything downstream fits on that envelope.
            var samples = SelectEnvelope(all);

            var recentFrom = now.AddDays(-LookbackDays);
            var recent = samples.Where(s => s.Time >= recentFrom).ToList();

            // Step 1: house behaviour over the long window, samples weighted by age.
            var weights = samples
                .Select(s => Math.Pow(0.5, (now - s.Time).TotalDays / TaperHalfLifeDays))
                .ToList();

            var longFit = samples.Count >= MinEnvelopeSamples
                ? SolveLeastSquares(BuildDesign(samples), samples.Select(s => s.Ratio).ToList(), weights)
                : null;

            if (longFit == null || longFit[1] >= 0.0)
            {
                // No usable long fit — fall back to fitting everything on the recent window.
                return FitAllOnRecent(recent.Count >= MinEnvelopeSamples ? recent : samples);
            }

            // Warmer meaning MORE power is not physical — drop that term rather than the whole
            // fit, so the SOC taper survives a noisy temperature season.
            double c = longFit[2] < 0.0 ? -longFit[2] : 0.0;
            double d = longFit[3] < 0.0 ? -longFit[3] : 0.0;

            // Step 2: battery taper on the recent window, temperature terms held fixed. Subtract
            // their contribution first, then a plain line through (soc, corrected ratio).
            var forSocFit = recent.Count >= MinSocOnlySamples ? recent : samples;

            var corrected = forSocFit
                .Select(s => (
                    s.Soc,
                    Ratio: s.Ratio
                         + c * (s.Temp - ChargeTaper.RefTemperatureC)
                         + d * (s.Mean48h - s.Temp)))
                .ToList();

            if (!TryFitLine(corrected, out double a, out double socSlope) || socSlope >= 0.0)
                return new ChargeTaper(longFit[0], -longFit[1], c, d, samples.Count);

            return new ChargeTaper(a, -socSlope, c, d, samples.Count);
        }

        /// <summary>
        /// Upper envelope of the samples: per SOC bin only the highest ratios.
        ///
        /// The batteries are commanded at the power the plan asks for, so a sample only touches
        /// the hardware limit when that request was high enough to reach it; every other sample
        /// measures the request itself. A mean through all of them therefore drags the fit down
        /// towards whatever was last asked for, the planner asks for less on the next solve, and
        /// the fit sinks again — between 28-07 and 05-08 that walked the requested share of
        /// nameplate from 76% down to 53% while the hardware delivered 100% of every request.
        /// With an envelope a low request can no longer lower the fit, while a single sample that
        /// did reach the limit raises it.
        /// </summary>
        internal static List<(DateTime Time, double Soc, double Temp, double Mean48h, double Ratio)> SelectEnvelope(
            IReadOnlyList<(DateTime Time, double Soc, double Temp, double Mean48h, double Ratio)> samples)
        {
            var envelope = new List<(DateTime Time, double Soc, double Temp, double Mean48h, double Ratio)>();

            foreach (var bin in samples.GroupBy(s => Math.Min((int)(s.Soc * EnvelopeBins), EnvelopeBins - 1)))
            {
                var ordered = bin.OrderByDescending(s => s.Ratio).ToList();

                // A band below the bin's best, never fewer than MinEnvelopePerBin points. A share
                // of the bin ("the top quarter") would not do: it lets more low samples in as they
                // arrive, which is exactly the sinking this has to stop.
                double floor = ordered[0].Ratio - EnvelopeBandRatio;
                int keep = Math.Max(MinEnvelopePerBin, ordered.Count(s => s.Ratio >= floor));

                envelope.AddRange(ordered.Take(Math.Min(keep, ordered.Count)));
            }

            return envelope.OrderBy(s => s.Time).ToList();
        }

        /// <summary>
        /// Fits all four coefficients on a single sample set — used when the two-window split is
        /// not available.
        /// </summary>
        private static ChargeTaper FitAllOnRecent(
            IReadOnlyList<(DateTime Time, double Soc, double Temp, double Mean48h, double Ratio)> samples)
        {
            if (samples.Count < MinEnvelopeSamples) return FitSocOnly(samples);

            var coefficients = SolveLeastSquares(BuildDesign(samples), samples.Select(s => s.Ratio).ToList());
            if (coefficients == null) return FitSocOnly(samples);

            if (coefficients[1] >= 0.0) return ChargeTaper.None;

            double c = coefficients[2] < 0.0 ? -coefficients[2] : 0.0;
            double d = coefficients[3] < 0.0 ? -coefficients[3] : 0.0;

            return new ChargeTaper(coefficients[0], -coefficients[1], c, d, samples.Count);
        }

        private static List<double[]> BuildDesign(
            IReadOnlyList<(DateTime Time, double Soc, double Temp, double Mean48h, double Ratio)> samples)
            => samples
                .Select(s => new[]
                {
                    1.0,
                    s.Soc,
                    s.Temp - ChargeTaper.RefTemperatureC,
                    s.Mean48h - s.Temp
                })
                .ToList();

        /// <summary>
        /// Loads every usable charge sample and pairs it with the temperature at that moment and
        /// the mean over the preceding 48 hours.
        /// </summary>
        private async Task<List<(DateTime Time, double Soc, double Temp, double Mean48h, double Ratio)>>
            CollectSamplesAsync(DateTime now, double capacity)
        {
            var historyStart = now.AddDays(-TaperHistoryDays);

            // Same rule as the buckets: an already-throttled denominator yields ratio ≈ 1.0 and
            // biases the fit towards no taper, and because MinRequestedW then drops the small
            // high-SOC requests it biases the SOC range as well.
            var plans = await _plannedQuarterDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(p => p.Time >= historyStart && p.Time <= now && p.PlannedUnthrottledPowerW != 0.0)
                    .ToList()));

            var measurements = await _measurementDataService.GetList(async set =>
                await Task.FromResult(set.Where(m => m.Time >= historyStart && m.Time <= now).ToList()));

            // Reaches back an extra 48 hours so the oldest sample still has a full window.
            var consumptions = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(c => c.Time >= historyStart.AddHours(-Mean48hHours) && c.Time <= now)
                    .ToList()));

            var foreignQuarters = await LoadForeignQuartersAsync(historyStart, now).ConfigureAwait(false);

            var planByTime = plans
                .GroupBy(p => p.Time)
                .ToDictionary(g => g.Key, g => g.First().PlannedUnthrottledPowerW);

            var temperatures = consumptions
                .Where(c => c.Temperature > -50.0)          // drop the -999 sentinel
                .OrderBy(c => c.Time)
                .Select(c => (c.Time, Temp: (double)c.Temperature))
                .ToList();

            // Prefix sums over the temperature series, so each 48-hour mean costs two binary
            // searches instead of a scan. Without this the fit is quadratic in the history
            // length, which stops being acceptable once there are years of data.
            var prefix = new double[temperatures.Count + 1];
            for (int i = 0; i < temperatures.Count; i++)
                prefix[i + 1] = prefix[i] + temperatures[i].Temp;

            var times = temperatures.Select(t => t.Time).ToList();

            var samples = new List<(DateTime, double, double, double, double)>();

            foreach (var m in measurements.OrderBy(m => m.Time))
            {
                // BatteryMode is our planned mode, not what the hardware chose (see
                // BatteriesService.StoreQuarterlyMeasurement), so it does not filter out quarters
                // someone else drove — this does.
                if (foreignQuarters.Contains(m.Time)) continue;
                if (m.BatteryMode != Modes.Charging) continue;
                if (!planByTime.TryGetValue(m.Time, out var requestedW)) continue;
                if (requestedW < MinRequestedW) continue;   // charge requests are positive

                double socFraction = m.BatteryStateOfChargeWh / capacity;
                if (socFraction < 0.0 || socFraction > 1.0) continue;

                if (!TryMean48h(times, temperatures, prefix, m.Time, out double temp, out double mean48h))
                    continue;

                // Clipping to 1.0 instead of dropping made the noise one-sided: an overshoot was
                // pulled back to 1.0 while an undershoot kept its full weight, so the fit could
                // only ever drift downwards.
                double ratio = Math.Abs(m.BatteryPowerWatts) / requestedW;
                if (ratio > MaxPlausibleRatio) continue;

                samples.Add((m.Time, socFraction, temp, mean48h, ratio));
            }

            return samples;
        }

        /// <summary>Least squares through (x, y) — returns false when x has no spread.</summary>
        private static bool TryFitLine(
            IReadOnlyList<(double Soc, double Ratio)> points, out double intercept, out double slope)
        {
            intercept = 0.0;
            slope = 0.0;

            int n = points.Count;
            if (n < 2) return false;

            double sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0;
            foreach (var (x, y) in points)
            {
                sx += x;
                sy += y;
                sxx += x * x;
                sxy += x * y;
            }

            double denominator = n * sxx - sx * sx;
            if (Math.Abs(denominator) < 1e-9) return false;

            slope = (n * sxy - sx * sy) / denominator;
            intercept = (sy - slope * sx) / n;
            return true;
        }

        /// <summary>
        /// Measured outside temperatures in a time range, oldest first. The planner needs these to
        /// build the 48-hour mean for quarters whose window reaches into the past; the forecast
        /// only covers the future.
        /// </summary>
        public async Task<List<(DateTime Time, double Temperature)>> GetMeasuredTemperaturesAsync(
            DateTime from, DateTime to)
        {
            var consumptions = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(c => c.Time >= from && c.Time <= to)
                    .ToList()));

            return consumptions
                .Where(c => c.Temperature > -50.0)          // drop the -999 sentinel
                .OrderBy(c => c.Time)
                .Select(c => (c.Time, (double)c.Temperature))
                .ToList();
        }

        /// <summary>Length of the heat build-up window, so callers can size their history query.</summary>
        public static int Mean48hWindowHours => Mean48hHours;

        /// <summary>
        /// Mean temperature over the 48 hours before <paramref name="time"/>, plus the temperature
        /// at that moment. False when either is missing.
        /// </summary>
        private static bool TryMean48h(
            List<DateTime> times,
            List<(DateTime Time, double Temp)> temperatures,
            double[] prefix,
            DateTime time,
            out double temperature,
            out double mean48h)
        {
            temperature = 0.0;
            mean48h = 0.0;

            int index = times.BinarySearch(time);
            if (index < 0) return false;                    // no measurement at this quarter

            temperature = temperatures[index].Temp;

            int from = times.BinarySearch(time.AddHours(-Mean48hHours));
            if (from < 0) from = ~from;                     // first entry inside the window

            int count = index - from + 1;
            if (count <= 0) return false;

            mean48h = (prefix[index + 1] - prefix[from]) / count;
            return true;
        }

        /// <summary>
        /// Fallback fit on SOC alone — used when the three-regressor system cannot be solved or
        /// there is not enough data for it.
        /// </summary>
        private static ChargeTaper FitSocOnly(
            IReadOnlyList<(DateTime Time, double Soc, double Temp, double Mean48h, double Ratio)> samples)
        {
            if (samples.Count < MinSocOnlySamples) return ChargeTaper.None;

            double sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0;
            foreach (var s in samples)
            {
                sx += s.Soc;
                sy += s.Ratio;
                sxx += s.Soc * s.Soc;
                sxy += s.Soc * s.Ratio;
            }

            int n = samples.Count;
            double denominator = n * sxx - sx * sx;
            if (Math.Abs(denominator) < 1e-9) return ChargeTaper.None;

            double slope = (n * sxy - sx * sy) / denominator;
            if (slope >= 0.0) return ChargeTaper.None;

            double intercept = (sy - slope * sx) / n;
            return new ChargeTaper(intercept, -slope, 0.0, 0.0, n);
        }

        /// <summary>
        /// Ordinary least squares via the normal equations, solved with Gaussian elimination and
        /// partial pivoting. Returns null when the system is singular or near-singular.
        /// </summary>
        internal static double[]? SolveLeastSquares(
            IReadOnlyList<double[]> design, IReadOnlyList<double> y, IReadOnlyList<double>? weights = null)
        {
            int k = design[0].Length;
            var matrix = new double[k, k + 1];

            for (int row = 0; row < k; row++)
            {
                for (int col = 0; col < k; col++)
                {
                    double sum = 0.0;
                    for (int i = 0; i < design.Count; i++)
                        sum += (weights?[i] ?? 1.0) * design[i][row] * design[i][col];
                    matrix[row, col] = sum;
                }

                double rhs = 0.0;
                for (int i = 0; i < design.Count; i++)
                    rhs += (weights?[i] ?? 1.0) * design[i][row] * y[i];
                matrix[row, k] = rhs;
            }

            for (int pivot = 0; pivot < k; pivot++)
            {
                int best = pivot;
                for (int row = pivot + 1; row < k; row++)
                    if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot]))
                        best = row;

                if (Math.Abs(matrix[best, pivot]) < 1e-9) return null;

                if (best != pivot)
                    for (int col = 0; col <= k; col++)
                        (matrix[pivot, col], matrix[best, col]) = (matrix[best, col], matrix[pivot, col]);

                for (int row = 0; row < k; row++)
                {
                    if (row == pivot) continue;
                    double factor = matrix[row, pivot] / matrix[pivot, pivot];
                    for (int col = pivot; col <= k; col++)
                        matrix[row, col] -= factor * matrix[pivot, col];
                }
            }

            var result = new double[k];
            for (int i = 0; i < k; i++)
                result[i] = matrix[i, k] / matrix[i, i];

            return result;
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