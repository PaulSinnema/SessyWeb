using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Derives two planner parameters from measured forecast error instead of leaving them to be
    /// guessed:
    ///
    ///   FutureValueDiscountPerHour  how much less a quarter further away is worth when the
    ///                               planner decides where to send stored energy. This is a
    ///                               statement about forecast uncertainty, so it should be
    ///                               measured on forecasts, not chosen.
    ///   NightReserveCapPct          how much energy to hold back for the night.
    ///
    /// Both come from the same 21-day window of ForecastSnapshot rows, which record the solar and
    /// consumption forecast at a series of lead times, joined with what actually happened. Solar
    /// and consumption are combined into one net-load error, because that is the quantity the
    /// planner actually plans against — an underestimated cloud and an overestimated dishwasher
    /// cancel out and should not both be charged as uncertainty.
    ///
    /// The discount fit: at lead time h the net-load forecast is off by e(h) of a typical
    /// quarter, over and above the error already present at zero lead (measurement noise, which
    /// no discount can help). A quarter that far out is then worth about (1 - e(h)) of its face
    /// value, and the planner's discount curve 1 / (1 + r * h) says the same thing at
    ///
    ///     r(h) = e(h) / (h * (1 - e(h)))
    ///
    /// The learned rate is the median of r(h) over the lead buckets that have enough samples —
    /// median, not mean, so one bad bucket cannot carry the result.
    ///
    /// The night reserve: the 80th percentile of what the house actually drew between 21:00 and
    /// 07:00 over the window. A percentile rather than the mean is where forecast error enters
    /// here: reserving the average night leaves the battery short on half of them.
    ///
    /// Bounds are deliberate. Below MinDiscountPerHour the discount stops doing anything and a
    /// single distant peak claims the whole battery again; above MaxDiscountPerHour the planner
    /// stops looking past tonight. Landing on a bound is not a result — it means the data is
    /// saying something the model cannot express — so it is logged as a warning and surfaced in
    /// Settings → Tips &amp; Checks rather than quietly applied.
    ///
    /// Learned values overwrite the configured ones, but only once there is something to learn
    /// from: until MinLearningDays of history exist both settings are left exactly as configured.
    /// A blind default written over a value that was tuned on measurements is a step backwards,
    /// and it would be rewritten every night for three weeks, so the field could never be
    /// corrected by hand in the meantime.
    /// </summary>
    public class PlannerLearningService
    {
        private readonly ForecastSnapshotDataService _forecastSnapshotDataService;
        private readonly ConsumptionDataService _consumptionDataService;
        private readonly InverterMeasurementDataService _inverterMeasurementDataService;
        private readonly SettingsDataService _settingsDataService;
        private readonly SettingsService _settingsService;
        private readonly IBatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;
        private readonly LoggingService<PlannerLearningService> _logger;

        // ── Windows and thresholds ────────────────────────────────────────────

        /// <summary>Window the fit runs over.</summary>
        private const int LookbackDays = 21;

        /// <summary>Days of snapshot history needed before anything is fitted.</summary>
        private const int MinLearningDays = 21;

        /// <summary>A lead bucket needs this many matched quarters to contribute.</summary>
        private const int MinBucketSamples = 40;

        /// <summary>Complete nights needed before the reserve is touched.</summary>
        private const int MinNights = 14;

        /// <summary>Hour the night window opens and closes (local time).</summary>
        private const int NightStartHour = 21;
        private const int NightEndHour = 7;

        /// <summary>Fraction of nights the reserve should cover.</summary>
        private const double NightReservePercentile = 80.0;

        // ── Bounds ────────────────────────────────────────────────────────────

        private const double MinDiscountPerHour = 0.005;
        private const double MaxDiscountPerHour = 0.03;
        private const double MinNightReservePct = 5.0;
        private const double MaxNightReservePct = 60.0;

        /// <summary>Hour of the day the nightly run happens, once the day's data is complete.</summary>
        private const int LearnAtHour = 3;

        /// <summary>Set when the last run had to clamp; read by ConfigurationCheckService.</summary>
        public string? PinnedWarning { get; private set; }

        public PlannerLearningService(
            ForecastSnapshotDataService forecastSnapshotDataService,
            ConsumptionDataService consumptionDataService,
            InverterMeasurementDataService inverterMeasurementDataService,
            SettingsDataService settingsDataService,
            SettingsService settingsService,
            IBatteryContainer batteryContainer,
            TimeZoneService timeZoneService,
            LoggingService<PlannerLearningService> logger)
        {
            _forecastSnapshotDataService = forecastSnapshotDataService;
            _consumptionDataService = consumptionDataService;
            _inverterMeasurementDataService = inverterMeasurementDataService;
            _settingsDataService = settingsDataService;
            _settingsService = settingsService;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
            _logger = logger;
        }

        /// <summary>
        /// Runs the fit once a day, after <see cref="LearnAtHour"/>. Cheap to call every cycle:
        /// it returns immediately when today's run already happened or learning is switched off.
        /// </summary>
        public async Task EnsureLearnedAsync()
        {
            var settings = _settingsService.Current;
            if (!settings.SelfLearningEnabled) return;

            var now = _timeZoneService.Now;
            if (now.Hour < LearnAtHour) return;
            if (settings.LastLearnedAt is DateTime last && last.Date >= now.Date) return;

            try
            {
                await LearnAsync(now).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Planner learning failed: {ex.ToDetailedString()}");
            }
        }

        private async Task LearnAsync(DateTime now)
        {
            var from = now.AddDays(-LookbackDays);

            var snapshots = await _forecastSnapshotDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(f => f.Time >= from && f.Time < now)
                    .ToList())).ConfigureAwait(false);

            var consumptions = await _consumptionDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(c => c.Time >= from && c.Time < now)
                    .ToList())).ConfigureAwait(false);

            var inverters = await _inverterMeasurementDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= from && m.Time < now)
                    .ToList())).ConfigureAwait(false);

            // Actual net load per quarter (Wh), mirroring QuarterlyInfo.NetLoadWh so the forecast
            // and the measurement are the same quantity.
            var solarWhByTime = inverters
                .GroupBy(m => m.Time)
                .ToDictionary(g => g.Key, g => g.Sum(m => m.SolarProductionKWh) * 1000.0);

            var actualNetWhByTime = consumptions
                .GroupBy(c => c.Time)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().ConsumptionWh * 0.25
                       - (solarWhByTime.TryGetValue(g.Key, out var s) ? s : 0.0));

            var (discount, discountPinned, discountNote) = FitDiscount(snapshots, actualNetWhByTime);
            var (reservePct, reservePinned, reserveNote) = FitNightReserve(
                actualNetWhByTime, _batteryContainer.GetTotalCapacity());

            await ApplyAsync(now, discount, reservePct,
                discountPinned || reservePinned,
                $"{discountNote} {reserveNote}".Trim()).ConfigureAwait(false);

            await PurgeOldSnapshotsAsync(now).ConfigureAwait(false);
        }

        // ── Discount ──────────────────────────────────────────────────────────

        /// <summary>
        /// Fitted discount, or null when there is not enough history to say anything — in which
        /// case the configured value is left exactly as it is. Writing a blind default over a
        /// value that was tuned on measurements would be a step backwards, and it would keep
        /// overwriting it every night for three weeks.
        /// </summary>
        internal static (double? Value, bool Pinned, string Note) FitDiscount(
            IReadOnlyList<ForecastSnapshot> snapshots,
            IReadOnlyDictionary<DateTime, double> actualNetWhByTime)
        {
            if (snapshots.Count == 0)
                return (null, false, "Discount: kept, no forecast history yet.");

            int daysCovered = (int)(snapshots.Max(s => s.Time) - snapshots.Min(s => s.Time)).TotalDays;
            if (daysCovered < MinLearningDays)
                return (null, false,
                    $"Discount: kept, {daysCovered} of {MinLearningDays} days collected.");

            // Mean absolute net-load error per lead bucket, plus the typical size of a quarter to
            // express it against.
            var errorSum = new Dictionary<int, double>();
            var errorCount = new Dictionary<int, int>();
            double scaleSum = 0.0;
            int scaleCount = 0;

            foreach (var snapshot in snapshots)
            {
                if (!actualNetWhByTime.TryGetValue(snapshot.Time, out double actual)) continue;

                double forecast = snapshot.ConsumptionForecastW * 0.25 - snapshot.SolarForecastW;
                double error = Math.Abs(forecast - actual);

                errorSum[snapshot.LeadHours] = errorSum.GetValueOrDefault(snapshot.LeadHours) + error;
                errorCount[snapshot.LeadHours] = errorCount.GetValueOrDefault(snapshot.LeadHours) + 1;

                scaleSum += Math.Abs(actual);
                scaleCount++;
            }

            if (scaleCount == 0 || scaleSum <= 0.0)
                return (null, false, "Discount: kept, no matched quarters.");

            double scale = scaleSum / scaleCount;

            // The error at zero lead is not forecast uncertainty — it is what the model gets
            // wrong about a quarter that has already arrived. Only the growth on top of it can
            // be bought off with a discount.
            double baseError = errorCount.TryGetValue(0, out int zeroCount) && zeroCount >= MinBucketSamples
                ? errorSum[0] / zeroCount
                : 0.0;

            var rates = new List<double>();

            foreach (var (lead, count) in errorCount)
            {
                if (lead <= 0 || count < MinBucketSamples) continue;

                double relative = Math.Max(0.0, errorSum[lead] / count - baseError) / scale;
                if (relative >= 0.9) relative = 0.9;      // beyond this the curve blows up

                rates.Add(relative / (lead * (1.0 - relative)));
            }

            if (rates.Count == 0)
                return (null, false, "Discount: kept, no lead bucket has enough samples.");

            rates.Sort();
            double fitted = rates[rates.Count / 2];

            double clamped = Math.Clamp(fitted, MinDiscountPerHour, MaxDiscountPerHour);
            bool pinned = Math.Abs(clamped - fitted) > 1e-9;

            string note = $"Discount: {clamped * 100.0:F2}%/h from {rates.Count} lead buckets"
                        + (pinned ? $" (fit was {fitted * 100.0:F2}%/h, clamped)." : ".");

            return (clamped, pinned, note);
        }

        // ── Night reserve ─────────────────────────────────────────────────────

        /// <summary>
        /// Fitted night reserve as a percentage of capacity, or null to leave the setting alone.
        /// </summary>
        internal static (double? Pct, bool Pinned, string Note) FitNightReserve(
            IReadOnlyDictionary<DateTime, double> actualNetWhByTime, double capacityWh)
        {
            if (capacityWh <= 0.0)
                return (null, false, "Night reserve: unchanged, battery capacity unknown.");

            // Group quarters by the night they belong to; a night is labelled by the date it
            // started, so the hours after midnight join the evening before them.
            var nights = new Dictionary<DateTime, double>();

            foreach (var (time, netWh) in actualNetWhByTime)
            {
                bool isNight = time.Hour >= NightStartHour || time.Hour < NightEndHour;
                if (!isNight) continue;

                var night = time.Hour < NightEndHour ? time.Date.AddDays(-1) : time.Date;
                nights[night] = nights.GetValueOrDefault(night) + Math.Max(0.0, netWh);
            }

            // The current night is still running and would look unusually cheap.
            var latest = nights.Keys.Count > 0 ? nights.Keys.Max() : DateTime.MinValue;
            nights.Remove(latest);

            if (nights.Count < MinNights)
                return (null, false, $"Night reserve: unchanged, {nights.Count} of {MinNights} nights measured.");

            var totals = nights.Values.OrderBy(v => v).ToList();
            double needWh = Percentiles.Percentile(totals, NightReservePercentile);
            double fitted = needWh / capacityWh * 100.0;

            double clamped = Math.Clamp(fitted, MinNightReservePct, MaxNightReservePct);
            bool pinned = Math.Abs(clamped - fitted) > 1e-9;

            string note = $"Night reserve: {clamped:F0}% (P{NightReservePercentile:F0} of {totals.Count} nights"
                        + (pinned ? $", fit was {fitted:F0}%, clamped)." : ").");

            return (clamped, pinned, note);
        }

        // ── Applying ──────────────────────────────────────────────────────────

        private async Task ApplyAsync(DateTime now, double? discount, double? reservePct, bool pinned, string note)
        {
            var stored = await _settingsDataService.GetList(async set =>
                await Task.FromResult(set.ToList())).ConfigureAwait(false);

            var settings = stored.FirstOrDefault();
            if (settings == null)
            {
                _logger.LogWarning("Planner learning: no Settings row to write to.");
                return;
            }

            // Null means the fit had nothing to say; the configured value stays untouched.
            if (discount.HasValue)
                settings.FutureValueDiscountPerHour = discount.Value;

            if (reservePct.HasValue)
                settings.NightReserveCapPct = reservePct.Value;

            settings.LastLearnedAt = now;
            settings.LastLearnedSummary = note;

            await _settingsDataService.AddOrUpdate([settings],
                (item, set) => set.FirstOrDefault(s => s.Id == item.Id)).ConfigureAwait(false);

            await _settingsService.RefreshAsync().ConfigureAwait(false);

            PinnedWarning = pinned ? note : null;

            if (pinned)
                _logger.LogWarning($"Planner learning hit a bound — {note}");
            else
                _logger.LogInformation($"Planner learning: {note}");
        }

        /// <summary>Drops snapshots the learner will never look at again.</summary>
        private async Task PurgeOldSnapshotsAsync(DateTime now)
        {
            var cutoff = now.AddDays(-ForecastSnapshot.MaxRetentionDays);

            var stale = await _forecastSnapshotDataService.GetList(async set =>
                await Task.FromResult(set.Where(f => f.Time < cutoff).ToList())).ConfigureAwait(false);

            if (stale.Count == 0) return;

            await _forecastSnapshotDataService.Remove(stale,
                (item, set) => set.FirstOrDefault(f => f.Id == item.Id)).ConfigureAwait(false);
        }
    }
}
