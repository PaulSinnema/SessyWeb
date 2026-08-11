using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using Endpoint = SessyCommon.Configurations.Endpoint;

namespace SessyController.Services.InverterServices
{
    /// <summary>
    /// Reads solar production from the Sessy batteries themselves instead of from an inverter.
    ///
    /// Every Sessy reports renewable_energy_phase1/2/3 from CT clamps around the PV group
    /// (SessyService.PowerStatus). That measurement existed all along but was only displayed, never
    /// used — so a household whose inverter SessyWeb cannot read had no solar term at all, and since
    /// consumption is solar + grid + battery, every quarter with net export came out negative and was
    /// discarded (issue #4).
    ///
    /// Implementing ISolarInverterService means nothing downstream has to know where the number came
    /// from: consumption, HardwareStatusService, the statistics, SolarPowerPage and the solar forecast
    /// all keep working unchanged.
    ///
    /// What it cannot do is curtail — see SupportsCurtailment.
    /// </summary>
    public class SessyInverterService : ISolarInverterService
    {
        /// <summary>Provider key in PowerSystems:Endpoints that activates this service.</summary>
        public const string SessyProviderName = "Sessy";

        private readonly LoggingService<SessyInverterService> _logger;
        private readonly IOptionsMonitor<PowerSystemsConfig> _powerSystemsConfigMonitor;
        private PowerSystemsConfig _powerSystemConfig { get; set; }
        private readonly IServiceScope _scope;
        private readonly TimeZoneService _timeZoneService;
        private readonly SettingsService _settingsService;
        private Settings _settingsConfig;
        private readonly InverterMeasurementWriter _measurementWriter;

        // Resolved on first use rather than injected: SolarInverterManager takes every
        // ISolarInverterService in its constructor, so reaching back to the batteries eagerly would
        // risk a cycle. Same reason the Modbus services resolve from their own scope.
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private BatteryContainer? _batteryContainer;

        public string ProviderName => SessyProviderName;

        /// <summary>
        /// This provider's endpoints, empty when it is not configured. Mirrors SunspecInverterService:
        /// DI constructs every inverter service, also the ones nobody uses.
        /// </summary>
        public Dictionary<string, Endpoint> Endpoints =>
            _powerSystemConfig?.Endpoints != null && _powerSystemConfig.Endpoints.TryGetValue(ProviderName, out var endpoints)
                ? endpoints
                : new Dictionary<string, Endpoint>();

        public double TotalCapacity => Endpoints.Sum(ep => ep.Value.InverterMaxCapacity);

        public double ActualSolarPowerInWatts { get; private set; }

        public bool IsAvailable { get; set; } = true;

        public DateTime LastSuccessfulReadUtc { get; private set; } = DateTime.MinValue;

        public bool SupportsFallback => false;

        public Task<double> GetFallbackACPowerInWattsAsync() => Task.FromResult(0.0);

        /// <summary>
        /// The Sessy measures production; it cannot reduce it. Everything that curtails must fall
        /// back rather than pretend — see EnergySystemStateMachine.EvaluateCurtailment.
        /// </summary>
        public bool SupportsCurtailment => false;

        /// <summary>
        /// Quarters in which the summed reading exceeded the configured array capacity, and the last
        /// one. Read by ConfigurationCheckService.
        ///
        /// One-sided evidence: a sum above the array's own capacity is physically impossible and so
        /// proves the same CT clamps are being counted more than once, but a sum below it proves
        /// nothing — three batteries each reporting a real 1500 W add up to 4500 W and stay under a
        /// 5000 W cap. Hence clamp and report, never silently accept.
        /// </summary>
        public int CapExceededSamples { get; private set; }
        public DateTime? LastCapExceededAt { get; private set; }

        /// <summary>
        /// Consecutive daylight samples in which every battery reported 0 W, and the last one.
        /// All-zero production while the sun is up means the CT clamps are not around the PV group —
        /// the install measures nothing and looks exactly like a house without panels.
        ///
        /// Consecutive, not cumulative: dawn and dusk legitimately read zero inside the daylight
        /// window, so a running total would eventually accuse a perfectly healthy installation. Any
        /// non-zero reading proves the clamps are there and clears it.
        /// </summary>
        public int ConsecutiveDaylightZeroSamples { get; private set; }
        public DateTime? LastDaylightZeroAt { get; private set; }

        /// <summary>One hour of samples at one per second — well past dawn and dusk.</summary>
        public const int DaylightZeroAlarmSamples = 3600;

        private bool _isRunning;
        private Dictionary<string, Dictionary<DateTime, double>> CollectedPowerData { get; set; } = new();

        public SessyInverterService(LoggingService<SessyInverterService> logger,
                                    SettingsService settingsService,
                                    IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor,
                                    IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _settingsService = settingsService;
            _settingsConfig = _settingsService.Current;
            _settingsService.SettingsChanged += (s, _) => _settingsConfig = s;

            _powerSystemsConfigMonitor = powerSystemsConfigMonitor;
            _powerSystemConfig = _powerSystemsConfigMonitor.CurrentValue;
            _powerSystemsConfigMonitor.OnChange((config) => _powerSystemConfig = config);

            _serviceScopeFactory = serviceScopeFactory;
            _scope = _serviceScopeFactory.CreateScope();

            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _measurementWriter = new InverterMeasurementWriter(
                _scope.ServiceProvider.GetRequiredService<InverterMeasurementDataService>(),
                _timeZoneService);
        }

        private BatteryContainer Batteries =>
            _batteryContainer ??= _scope.ServiceProvider.GetRequiredService<BatteryContainer>();

        // ── Power ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Total PV production over the selected batteries, clamped to the configured array capacity.
        ///
        /// Summing is safe for the usual wiring, where only one Sessy carries the CT clamps: a
        /// battery without them reports exactly 0 W and contributes nothing. Where every Sessy sees
        /// the same clamps the sum runs into the cap, which is counted and surfaced in Tips & Checks;
        /// the Batteries field on the endpoint narrows it down explicitly.
        /// </summary>
        public async Task<double> GetTotalACPowerInWatts()
        {
            var batteries = SelectBatteries();

            if (batteries.Count == 0)
            {
                // Either no battery at all, or the configured selection matches none of them. Both
                // are "cannot read", not "no production" — 0 W here would be stored as a measurement.
                IsAvailable = false;
                return 0.0;
            }

            double total = 0.0;
            bool anyAnswered = false;

            foreach (var battery in batteries)
            {
                try
                {
                    var status = await battery.GetPowerStatus().ConfigureAwait(false);

                    if (status == null)
                        continue;

                    anyAnswered = true;
                    total += SumRenewablePhases(status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Sessy solar: could not read battery {battery.Id} — {ex.Message}");
                }
            }

            // No battery answered: 0 W here would read as darkness in the consumption sum, so say
            // unavailable instead and let SolarIsMeasurable drop the sample.
            if (!anyAnswered)
            {
                IsAvailable = false;
                return 0.0;
            }

            IsAvailable = true;
            LastSuccessfulReadUtc = _timeZoneService.Now;

            return Clamp(total);
        }

        /// <summary>
        /// The batteries this source reads, after applying the endpoint's optional Batteries filter.
        /// Also records which requested keys match no configured battery, so Tips & Checks can name
        /// them — a typo that silently widened the selection back to "all" would defeat the purpose
        /// of asking for a subset.
        /// </summary>
        private List<Battery> SelectBatteries()
        {
            var configured = Batteries.Batteries ?? new List<Battery>();

            var requested = Endpoints.Values
                .Where(ep => ep.Batteries != null)
                .SelectMany(ep => ep.Batteries!)
                .Distinct()
                .ToList();

            UnknownBatteryIds = SelectBatteryIds(configured.Select(b => b.Id), requested, out var selectedIds);

            if (selectedIds == null)
                return configured.ToList();

            return configured.Where(b => selectedIds.Contains(b.Id)).ToList();
        }

        /// <summary>
        /// Applies the selection rule. Static and pure so it can be tested without hardware.
        /// <paramref name="selectedIds"/> is null when everything is selected — an empty or absent
        /// filter means all batteries, which is not the same as selecting none.
        /// Returns the requested keys that match no configured battery.
        /// </summary>
        public static List<string> SelectBatteryIds(IEnumerable<string> configuredIds,
                                                    IEnumerable<string>? requestedIds,
                                                    out HashSet<string>? selectedIds)
        {
            var requested = requestedIds?.ToList() ?? new List<string>();

            if (requested.Count == 0)
            {
                selectedIds = null;
                return new List<string>();
            }

            var configured = new HashSet<string>(configuredIds, StringComparer.OrdinalIgnoreCase);

            selectedIds = new HashSet<string>(requested.Where(configured.Contains), StringComparer.OrdinalIgnoreCase);

            return requested.Where(id => !configured.Contains(id)).ToList();
        }

        /// <summary>
        /// Battery keys named in the endpoint's Batteries field that do not exist in Sessy:Batteries.
        /// Reported by ConfigurationCheckService.
        /// </summary>
        public List<string> UnknownBatteryIds { get; private set; } = new();

        /// <summary>
        /// Sum of the three renewable phases in Watts. Static and pure — the whole combining rule in
        /// one testable place.
        /// </summary>
        public static double SumRenewablePhases(PowerStatus status)
        {
            double total = 0.0;

            total += status.RenewableEnergyPhase1?.Power ?? 0;
            total += status.RenewableEnergyPhase2?.Power ?? 0;
            total += status.RenewableEnergyPhase3?.Power ?? 0;

            return total;
        }

        /// <summary>
        /// Caps a reading at the configured array capacity. Static and pure so the rule can be tested
        /// without hardware; the counters live with the caller.
        /// </summary>
        public static double ClampToCapacity(double totalW, double capacityW, out bool exceeded)
        {
            exceeded = capacityW > 0.0 && totalW > capacityW;

            return exceeded ? capacityW : totalW;
        }

        private double Clamp(double totalW)
        {
            var result = ClampToCapacity(totalW, TotalCapacity, out bool exceeded);

            if (exceeded)
            {
                CapExceededSamples++;
                LastCapExceededAt = _timeZoneService.Now;
            }

            return result;
        }

        /// <summary>Single endpoint reads the same total — the batteries are not split per inverter id.</summary>
        public Task<double> GetACPowerInWatts(string id) => GetTotalACPowerInWatts();

        /// <summary>No Modbus status register exists here; 1 means "running" for display purposes.</summary>
        public Task<ushort> GetStatus(string id) => Task.FromResult((ushort)(IsAvailable ? 1 : 0));

        /// <summary>
        /// Nothing to write to. Logged rather than ignored, because a silent no-op here is exactly
        /// the failure mode SupportsCurtailment exists to prevent.
        /// </summary>
        public Task ThrottleInverterToPercentage(ushort percentage)
        {
            _logger.LogWarning($"Sessy solar: throttle to {percentage}% ignored — the Sessy measures " +
                               "production but cannot reduce it.");
            return Task.CompletedTask;
        }

        // ── Sampling loop ─────────────────────────────────────────────────────

        public async Task Start(CancellationToken cancelationToken)
        {
            if (Endpoints.Count == 0)
                return;

            _isRunning = true;

            _logger.LogWarning("Sessy solar service started ...");

            _ = Task.Run(() => RunAsync(cancelationToken), cancelationToken);

            await Task.Yield();
        }

        public async Task Stop(CancellationToken cancelationToken)
        {
            _isRunning = false;
            await Task.Yield();
        }

        private async Task RunAsync(CancellationToken cancelationToken)
        {
            while (_isRunning && !cancelationToken.IsCancellationRequested)
            {
                try
                {
                    await Process().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while reading Sessy solar data.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancelationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay.
                }
            }

            _logger.LogWarning("Sessy solar service stopped.");
        }

        private async Task Process()
        {
            bool daylight = _timeZoneService.GetSunlightLevel(_settingsConfig.Latitude, _settingsConfig.Longitude)
                            == SolCalc.Data.SunlightLevel.Daylight;

            if (!daylight)
            {
                // Outside daylight hours — reset to zero, same as the Modbus services.
                ActualSolarPowerInWatts = 0.0;
                return;
            }

            double reading = await GetTotalACPowerInWatts().ConfigureAwait(false);

            if (!IsAvailable)
                return;

            ActualSolarPowerInWatts = reading;

            if (reading <= 0.0)
            {
                ConsecutiveDaylightZeroSamples++;
                LastDaylightZeroAt = _timeZoneService.Now;
            }
            else
            {
                ConsecutiveDaylightZeroSamples = 0;
            }

            var id = Endpoints.Keys.FirstOrDefault() ?? "1";

            if (!CollectedPowerData.ContainsKey(id))
                CollectedPowerData.Add(id, new Dictionary<DateTime, double>());

            CollectedPowerData[id][_timeZoneService.Now] = reading;

            await _measurementWriter.StoreAsync(ProviderName, CollectedPowerData).ConfigureAwait(false);
        }
    }
}
