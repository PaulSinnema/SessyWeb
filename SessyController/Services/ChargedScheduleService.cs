using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;

namespace SessyController.Services
{
    /// <summary>
    /// Reads the schedule the Sessy batteries plan for themselves and turns it into quarters.
    ///
    /// The batteries keep planning whether or not they are the ones executing, so this is read in
    /// both directions: while Charged is in control it is what will actually happen (our MILP plan
    /// is not executed then, and drawing it as if it were is fiction), and while SessyWeb is in
    /// control it is the alternative we chose not to take — drawn as a shadow so the two plans can
    /// be compared on the same axes.
    ///
    /// Fetching a schedule is a plain read, so it also runs in a debug session; only handing over
    /// the actual control stays behind the DEBUG guard in BatteriesService.
    ///
    /// Two details that the previous inline version got wrong:
    ///   * it read one battery and presented that power as the system total, so everything showed
    ///     at roughly a third of its real height. Every battery plans its own schedule, so they are
    ///     summed here;
    ///   * the sign convention is Sessy's: negative is charging, positive is discharging.
    /// </summary>
    public class ChargedScheduleService
    {
        private readonly LoggingService<ChargedScheduleService> _logger;
        private readonly BatteryContainer _batteryContainer;
        private readonly TimeZoneService _timeZoneService;
        private readonly SettingsService _settingsService;

        public ChargedScheduleService(
            LoggingService<ChargedScheduleService> logger,
            BatteryContainer batteryContainer,
            TimeZoneService timeZoneService,
            SettingsService settingsService)
        {
            _logger = logger;
            _batteryContainer = batteryContainer;
            _timeZoneService = timeZoneService;
            _settingsService = settingsService;
        }

        /// <summary>
        /// How often the batteries are asked for their schedule. They recompute it around the hour,
        /// so polling it every cycle would be three HTTP calls a minute for a number that barely
        /// moves.
        /// </summary>
        private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);

        /// <summary>Signed power (W) per quarter as last fetched: negative charges, positive discharges.</summary>
        public IReadOnlyDictionary<DateTime, double> PowerByQuarter { get; private set; }
            = new Dictionary<DateTime, double>();

        /// <summary>When the schedule was last read from the batteries; null while nothing was read.</summary>
        public DateTime? LastFetched { get; private set; }

        private DateTime? _lastAttempt;

        /// <summary>True when the last fetch produced at least one quarter.</summary>
        public bool HasSchedule => PowerByQuarter.Count > 0;

        /// <summary>
        /// Fetches the schedule from every battery and sums it per quarter. Returns the result and
        /// keeps it, so the UI can read it without triggering hardware calls of its own.
        /// </summary>
        public async Task<IReadOnlyDictionary<DateTime, double>> RefreshAsync(bool force = false)
        {
            var batteries = _batteryContainer.Batteries;

            if (batteries == null || batteries.Count == 0)
                return PowerByQuarter;

            // Throttle on the attempt, not on the last success: a battery that keeps returning an
            // empty schedule would otherwise be polled every cycle.
            if (!force && _lastAttempt != null && _timeZoneService.Now - _lastAttempt < MinRefreshInterval)
                return PowerByQuarter;

            _lastAttempt = _timeZoneService.Now;

            // Same endpoint either way; SessyService guards each method with the control mode it
            // belongs to, so asking for the wrong one simply returns null. The batteries keep
            // planning for themselves while we drive them, which is what makes the comparison on
            // the chart possible in both directions.
            bool chargedInControl = _settingsService.Current.ChargedInControl;

            var totals = new Dictionary<DateTime, double>();

            foreach (var battery in batteries)
            {
                try
                {
                    var schedule = chargedInControl
                        ? await battery.GetChargedScheduleAsync().ConfigureAwait(false)
                        : await battery.GetDynamicScheduleAsync().ConfigureAwait(false);

                    if (schedule?.DynamicSchedule == null) continue;

                    Merge(totals, ToQuarters(schedule.DynamicSchedule));
                }
                catch (Exception ex)
                {
                    // One unreachable battery must not throw away the other two.
                    _logger.LogWarning($"Could not read the Sessy schedule from battery {battery.Id}: {ex.Message}");
                }
            }

            if (totals.Count > 0)
            {
                PowerByQuarter = totals;
                LastFetched = _timeZoneService.Now;
            }

            return PowerByQuarter;
        }

        /// <summary>
        /// Expands schedule blocks into quarters. A block spans a range, so an hour of 2000 W
        /// becomes four quarters of 2000 W — the power is a rate, not an amount, so it is not
        /// divided over the quarters it covers.
        /// </summary>
        public static Dictionary<DateTime, double> ToQuarters(IEnumerable<DynamicScheduleItem> schedule)
        {
            var result = new Dictionary<DateTime, double>();

            foreach (var item in schedule)
            {
                var start = item.StartTime.DateFloorQuarter();
                var end = item.EndTime;

                for (var time = start; time < end; time = time.AddMinutes(15))
                {
                    result.TryGetValue(time, out var running);
                    result[time] = running + item.Power;
                }
            }

            return result;
        }

        private static void Merge(Dictionary<DateTime, double> totals, Dictionary<DateTime, double> addition)
        {
            foreach (var (time, power) in addition)
            {
                totals.TryGetValue(time, out var running);
                totals[time] = running + power;
            }
        }

        /// <summary>
        /// Walks the schedule forward from the measured state of charge and returns the expected
        /// SOC (Wh) at the end of each quarter. No clock and no database, so it can be tested on
        /// its own — same shape as ChargeCostBasisService.Project.
        /// </summary>
        /// <param name="fromQuarter">First quarter to project; earlier entries are ignored.</param>
        /// <param name="socWh">Measured state of charge at the start of that quarter.</param>
        /// <param name="capacityWh">Total battery capacity.</param>
        /// <param name="chargeEfficiency">AC to stored; discharge divides by it in reverse.</param>
        public static Dictionary<DateTime, double> Project(
            IReadOnlyDictionary<DateTime, double> powerByQuarter,
            DateTime fromQuarter,
            double socWh,
            double capacityWh,
            double chargeEfficiency = 1.0,
            double dischargeEfficiency = 1.0)
        {
            var result = new Dictionary<DateTime, double>();

            if (capacityWh <= 0.0) return result;

            double chEff = Clamp(chargeEfficiency, 0.05, 1.0);
            double disEff = Clamp(dischargeEfficiency, 0.05, 1.0);
            double soc = Clamp(socWh, 0.0, capacityWh);

            foreach (var time in powerByQuarter.Keys.Where(t => t >= fromQuarter).OrderBy(t => t))
            {
                double powerW = powerByQuarter[time];
                double energyWh = powerW * 0.25;    // a quarter of an hour at that power

                // Sessy's sign convention: negative charges the battery.
                soc = energyWh < 0.0
                    ? soc + Math.Abs(energyWh) * chEff
                    : soc - energyWh / disEff;

                soc = Clamp(soc, 0.0, capacityWh);
                result[time] = soc;
            }

            return result;
        }

        private static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);
    }
}
