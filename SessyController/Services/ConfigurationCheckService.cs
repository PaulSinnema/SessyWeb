using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Managers;
using SessyController.Services.InverterServices;
using SessyController.Services.Items;
using SessyController.Services.Statistics;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Runs all configuration checks and returns a list of results
    /// for display on the Tips & Checks tab in Settings.
    /// </summary>
    public class ConfigurationCheckService
    {
        private readonly IConfiguration _configuration;
        private readonly TaxesDataService _taxesDataService;
        private readonly IGasPricesDataService _gasPricesDataService;
        private readonly IEPEXPricesService _epexPricesService;
        private readonly TimeZoneService _timeZoneService;
        // Monitor rather than IOptions: the checks must reflect appsettings.json as it is now, not
        // as it was when this service was first resolved.
        private readonly IOptionsMonitor<HeatPumpConfig> _heatPumpConfigMonitor;
        private HeatPumpConfig _heatPumpConfig => _heatPumpConfigMonitor.CurrentValue;
        private readonly IMilpService _milpService;
        private readonly SettingsService _settingsService;
        private readonly PlannerLearningService _plannerLearningService;
        private readonly InvestmentDataService _investmentDataService;
        private readonly InvestmentGroupDataService _investmentGroupDataService;
        private readonly SystemCapabilitiesService _capabilities;
        private readonly ThrottleAnalysisService _throttleAnalysisService;
        private readonly IOptionsMonitor<SessyBatteryConfig> _batteryConfig;
        private readonly IOptionsMonitor<WeatherExpectancyConfig> _weatherConfigMonitor;
        private WeatherExpectancyConfig _weatherConfig => _weatherConfigMonitor.CurrentValue;
        private readonly IOptionsMonitor<SessyP1Config> _p1ConfigMonitor;
        private SessyP1Config _p1Config => _p1ConfigMonitor.CurrentValue;
        private readonly WeatherService _weatherService;
        private readonly P1MeterContainer _p1MeterContainer;
        private readonly ConsumptionDataService _consumptionDataService;
        private readonly SolarInverterManager _solarInverterManager;
        private readonly ConsumptionMonitorService _consumptionMonitorService;
        private readonly SessyInverterService _sessyInverterService;
        private readonly BatteriesService _batteriesService;
        private readonly IOptionsMonitor<PowerSystemsConfig> _powerSystemsConfigMonitor;
        private PowerSystemsConfig _powerSystemsConfig => _powerSystemsConfigMonitor.CurrentValue;

        public ConfigurationCheckService(
            IConfiguration configuration,
            TaxesDataService taxesDataService,
            IGasPricesDataService gasPricesDataService,
            IEPEXPricesService epexPricesService,
            TimeZoneService timeZoneService,
            IOptionsMonitor<HeatPumpConfig> heatPumpConfigMonitor,
            IMilpService milpService,
            SettingsService settingsService,
            PlannerLearningService plannerLearningService,
            InvestmentDataService investmentDataService,
            InvestmentGroupDataService investmentGroupDataService,
            SystemCapabilitiesService capabilities,
            ThrottleAnalysisService throttleAnalysisService,
            IOptionsMonitor<SessyBatteryConfig> batteryConfig,
            IOptionsMonitor<WeatherExpectancyConfig> weatherConfigMonitor,
            IOptionsMonitor<SessyP1Config> p1ConfigMonitor,
            WeatherService weatherService,
            P1MeterContainer p1MeterContainer,
            ConsumptionDataService consumptionDataService,
            SolarInverterManager solarInverterManager,
            ConsumptionMonitorService consumptionMonitorService,
            SessyInverterService sessyInverterService,
            BatteriesService batteriesService,
            IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor)
        {
            _batteriesService = batteriesService;
            _solarInverterManager = solarInverterManager;
            _consumptionMonitorService = consumptionMonitorService;
            _sessyInverterService = sessyInverterService;
            _powerSystemsConfigMonitor = powerSystemsConfigMonitor;
            _weatherConfigMonitor = weatherConfigMonitor;
            _p1ConfigMonitor = p1ConfigMonitor;
            _weatherService = weatherService;
            _p1MeterContainer = p1MeterContainer;
            _consumptionDataService = consumptionDataService;
            _investmentDataService = investmentDataService;
            _investmentGroupDataService = investmentGroupDataService;
            _capabilities = capabilities;
            _throttleAnalysisService = throttleAnalysisService;
            _batteryConfig = batteryConfig;
            _configuration = configuration;
            _taxesDataService = taxesDataService;
            _gasPricesDataService = gasPricesDataService;
            _epexPricesService = epexPricesService;
            _timeZoneService = timeZoneService;
            _heatPumpConfigMonitor = heatPumpConfigMonitor;
            _milpService = milpService;
            _settingsService = settingsService;
            _plannerLearningService = plannerLearningService;
        }

        // ── Summary for the badge ────────────────────────────────────────────
        //
        // The checks only ran when someone opened the Tips & Checks tab, which is exactly the
        // moment nobody looks. Keeping the counts of the last run lets the menu and the tab header
        // say there is something to see, without every render re-querying the database.

        /// <summary>How long the counts stay good enough to badge with.</summary>
        private static readonly TimeSpan SummaryMaxAge = TimeSpan.FromMinutes(5);

        private readonly SemaphoreSlim _summaryLock = new(1, 1);
        private DateTime _lastRunAt = DateTime.MinValue;

        public int ErrorCount { get; private set; }
        public int WarningCount { get; private set; }
        public bool HasIssues => ErrorCount > 0 || WarningCount > 0;

        /// <summary>Raised after every run so open circuits can refresh their badge.</summary>
        public event Action? ChecksChanged;

        /// <summary>
        /// Makes sure the counts are recent enough to show. Runs the checks only when the last run
        /// is older than <see cref="SummaryMaxAge"/>, and only one run at a time — in Blazor Server
        /// every open browser tab is its own circuit and they all ask at once.
        /// </summary>
        public async Task EnsureSummaryAsync()
        {
            if (_timeZoneService.Now - _lastRunAt < SummaryMaxAge) return;

            await _summaryLock.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_timeZoneService.Now - _lastRunAt < SummaryMaxAge) return;

                await RunAllChecksAsync().ConfigureAwait(false);
            }
            finally
            {
                _summaryLock.Release();
            }
        }

        public async Task<List<ConfigurationCheck>> RunAllChecksAsync()
        {
            var checks = new List<ConfigurationCheck>();

            CheckWeatherConfiguration(checks);
            CheckP1MeterConfiguration(checks);
            CheckBatteryConfiguration(checks);
            CheckSolarSource(checks);
            CheckSolarMeasurement(checks);
            CheckBatteryStrategyStability(checks);
            AddBadgeDemoChecks(checks);
            await CheckConsumptionHistory(checks);
            await CheckEneverToken(checks);
            await CheckTaxesConfiguration(checks);
            await CheckGasPricesHistory(checks);
            CheckHeatPumpConfiguration(checks);
            await CheckChargeTaper(checks);
            await CheckInvestmentsHaveTheirSavingsSource(checks);
            CheckSettingsExtremes(checks);
            CheckPlannerLearning(checks);
            await CheckPlanStatus(checks).ConfigureAwait(false);

            int errors = checks.Count(c => c.Severity == CheckSeverity.Error);
            int warnings = checks.Count(c => c.Severity == CheckSeverity.Warning);

            bool changed = errors != ErrorCount || warnings != WarningCount;

            ErrorCount = errors;
            WarningCount = warnings;
            _lastRunAt = _timeZoneService.Now;

            // Only on a real change. The Settings page re-runs every 5 seconds while it is open,
            // and rendering the layout renders @Body with it (v1.0.76) — an unconditional event
            // would redraw the whole page four times a minute for nothing.
            if (changed)
                ChecksChanged?.Invoke();

            return checks.OrderBy(c => c.Severity).ToList();
        }

        /// <summary>
        /// Consumption is still recorded without weather (stored as the -999 sentinel), but the
        /// records lose the temperature, humidity and radiation the consumption estimate matches on,
        /// so the planner falls back to the monthly profile. Worth saying out loud: until v1.0.96 a
        /// missing feed stopped recording altogether, which is what issue #4 reported.
        /// </summary>
        private void CheckWeatherConfiguration(List<ConfigurationCheck> checks)
        {
            var config = _weatherConfig;

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(config.BaseUrl)) missing.Add("BaseUrl");
            if (string.IsNullOrWhiteSpace(config.APIKey)) missing.Add("APIKey");
            if (string.IsNullOrWhiteSpace(config.Location)) missing.Add("Location");

            if (missing.Count > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Weather service not configured",
                    Description = $"The WeerOnline section of appsettings.json is missing {string.Join(", ", missing)}. " +
                                  "Consumption is still recorded, but without temperature, humidity and radiation, " +
                                  "so the consumption estimate has nothing to match on and the planner falls back to " +
                                  "the monthly energy profile from Settings. The solar forecast is unavailable too. " +
                                  "Add APIKey, BaseUrl and Location.",
                    ActionUrl = "https://weerlive.nl/delen.php",
                    ActionLabel = "Get free API key"
                });
                return;
            }

            if (!_weatherService.IsInitialized())
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Weather service configured but no data received",
                    Description = $"No weather data has been fetched for location '{config.Location}'. " +
                                  "Right after a start this is normal — the first fetch takes a moment. If it " +
                                  "persists, check the API key, the location name and whether the API day limit " +
                                  "was reached. Consumption keeps being recorded, without weather values.",
                });
                return;
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Info,
                Title = "Weather service active",
                Description = $"Weather data is being fetched for '{config.Location}'."
            });
        }

        /// <summary>
        /// No meter means no consumption recording, and — since (dis)charging now runs through the
        /// P1 grid target — no battery control either. Absence is an Error, not a Warning.
        /// </summary>
        private void CheckP1MeterConfiguration(List<ConfigurationCheck> checks)
        {
            var endpoints = _p1Config.Endpoints;
            var skipped = endpoints.Where(ep => !ep.Value.IsConfigured).Select(ep => ep.Key).ToList();
            int configured = endpoints.Count - skipped.Count;

            if (endpoints.Count == 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "No P1 meter configured",
                    Description = "The Sessy:Meters section of appsettings.json is empty or absent. Household " +
                                  "consumption is measured through the P1 meter, and (dis)charging is now driven " +
                                  "through its grid target, so nothing is recorded and the batteries cannot be " +
                                  "controlled. Add a meter with Name, BaseUrl, UserId and Password."
                });
                return;
            }

            if (configured == 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "P1 meter has no address",
                    Description = $"The Sessy:Meters entries ({string.Join(", ", skipped)}) have no BaseUrl, so they " +
                                  "are skipped. This happens when credentials are left behind in secrets.json for a " +
                                  "meter that appsettings.json no longer declares — secrets augment a device, they do " +
                                  "not declare one. No consumption is recorded and the batteries cannot be controlled."
                });
                return;
            }

            if (skipped.Count > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = $"{skipped.Count} P1 meter entry without an address",
                    Description = $"Entries {string.Join(", ", skipped)} have no BaseUrl and are skipped. Usually a " +
                                  "leftover in secrets.json for a meter that was removed from appsettings.json."
                });
            }

            // The container rebuilds its list on a config change; a mismatch means it did not take.
            int live = _p1MeterContainer.P1Meters?.Count ?? 0;

            if (live < configured)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Configured P1 meters are not all in use",
                    Description = $"{configured} meter(s) are configured but {live} are active. Restart the " +
                                  "application if the configuration was changed while it was running."
                });
                return;
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Info,
                Title = $"P1 meter active ({live})",
                Description = "Household consumption is measured through the P1 meter."
            });
        }

        /// <summary>
        /// Consumption is computed as solar + grid + battery, and a battery that cannot be reached
        /// makes the whole quarter fall back to zero — so a missing battery costs consumption data
        /// as well as planning.
        /// </summary>
        private void CheckBatteryConfiguration(List<ConfigurationCheck> checks)
        {
            var config = _batteryConfig.CurrentValue;
            var skipped = config.Batteries.Where(bat => !bat.Value.IsConfigured).Select(bat => bat.Key).ToList();
            int configured = config.ConfiguredBatteries.Count();

            if (config.Batteries.Count == 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "No battery configured",
                    Description = "The Sessy:Batteries section of appsettings.json is empty or absent. Without a " +
                                  "battery there is nothing to plan and nothing to steer."
                });
                return;
            }

            if (configured == 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Batteries have no address",
                    Description = $"The Sessy:Batteries entries ({string.Join(", ", skipped)}) have no BaseUrl and " +
                                  "are skipped — usually credentials left behind in secrets.json for a battery that " +
                                  "appsettings.json no longer declares."
                });
                return;
            }

            if (skipped.Count > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = $"{skipped.Count} battery entry without an address",
                    Description = $"Entries {string.Join(", ", skipped)} have no BaseUrl and are skipped, so they add " +
                                  "no capacity. Remove them from secrets.json, or declare them in appsettings.json."
                });
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Info,
                Title = $"Batteries configured ({configured})",
                Description = $"Total capacity {config.TotalCapacity / 1000.0:F1} kWh, " +
                              $"charge {config.TotalRawChargingCapacity:F0} W, " +
                              $"discharge {config.TotalRawDischargingCapacity:F0} W."
            });
        }

        /// <summary>
        /// Which source measures the panels, and what that choice costs. The Sessy CT clamps and an
        /// inverter see the same panels, so they are an either/or; and the Sessy can only measure,
        /// which means curtailment at negative prices is gone. Both are worth saying out loud rather
        /// than leaving in a log line nobody reads.
        /// </summary>
        private void CheckSolarSource(List<ConfigurationCheck> checks)
        {
            var providers = _powerSystemsConfig.Endpoints
                .Where(ep => ep.Value.Count > 0)
                .Select(ep => ep.Key)
                .ToList();

            bool sessyConfigured = providers.Contains(SessyInverterService.SessyProviderName);

            if (!sessyConfigured)
                return;

            var others = providers.Where(p => p != SessyInverterService.SessyProviderName).ToList();

            if (others.Count > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Two solar sources configured",
                    Description = $"PowerSystems declares both '{SessyInverterService.SessyProviderName}' and " +
                                  $"{string.Join(", ", others)}. They measure the same panels, so counting both " +
                                  "would double every reading — only the Sessy is used and the rest is ignored. " +
                                  "Remove whichever you do not want, and note that curtailment needs the inverter."
                });
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Warning,
                Title = "Solar is measured through the Sessy — no curtailment",
                Description = "The Sessy reports production from its CT clamps but cannot reduce it. At negative " +
                              "prices the inverter is therefore not throttled: the battery absorbs what it can, " +
                              "and anything above that is exported at the negative price. Configure the inverter " +
                              "under PowerSystems if you want curtailment back."
            });

            if (_sessyInverterService.UnknownBatteryIds.Count > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Solar reads from batteries that do not exist",
                    Description = $"The Batteries field under PowerSystems:Endpoints:Sessy names " +
                                  $"{string.Join(", ", _sessyInverterService.UnknownBatteryIds)}, which is not a key in " +
                                  "Sessy:Batteries. Those entries select nothing. If none of the named batteries " +
                                  "exists, no solar is read at all — deliberately, because quietly falling back to " +
                                  "every battery would ignore the very reason for naming a subset. Remove the field " +
                                  "to use all batteries."
                });
            }

            if (_sessyInverterService.ConsecutiveDaylightZeroSamples >= SessyInverterService.DaylightZeroAlarmSamples)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Sessy reports no solar production",
                    Description = $"Every battery has reported 0 W for over an hour of daylight, most recently " +
                                  $"{_sessyInverterService.LastDaylightZeroAt:dd-MM-yyyy HH:mm}. The CT clamps are " +
                                  "not around the PV group, so nothing is being measured and consumption looks " +
                                  "exactly like a house without panels. Check the clamps on at least one Sessy."
                });
            }

            if (_sessyInverterService.CapExceededSamples > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Sessy solar exceeds the configured array capacity",
                    Description = $"{_sessyInverterService.CapExceededSamples} reading(s) came out above " +
                                  $"InverterMaxCapacity, most recently {_sessyInverterService.LastCapExceededAt:dd-MM-yyyy HH:mm}; " +
                                  "they were clamped. More than the array can physically produce means either several " +
                                  "Sessys are measuring the same CT clamps, or InverterMaxCapacity is set too low. " +
                                  "Note that the reverse proves nothing: double counting below the cap is invisible."
                });
            }
        }

        /// <summary>
        /// Consumption is solar + grid + battery. Panels that SessyWeb cannot read leave the solar
        /// term at 0, which is invisible until the house exports: the sum then goes negative and the
        /// quarter is discarded, so consumption is recorded at night and missing all day (issue #4).
        /// A discarded quarter is the evidence, which is why it is counted rather than only logged.
        /// </summary>
        // ═════════════════════════════════════════════════════════════════════
        // TEMPORARY — remove. Only here to show what the notification dot looks
        // like; DEBUG-only so it can never reach the published image.
        // ═════════════════════════════════════════════════════════════════════
        private static void AddBadgeDemoChecks(List<ConfigurationCheck> checks)
        {
#if DEBUG
            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Error,
                Title = "TEMPORARY — demo error",
                Description = "Not a real problem. This check exists only to make the notification dot " +
                              "appear on the Settings menu item and on the Tips & Checks tab. It is " +
                              "compiled into DEBUG builds only, so it is not in the Docker image. " +
                              "Delete AddBadgeDemoChecks and its call in RunAllChecksAsync when done."
            });
#endif
        }

        /// <summary>
        /// Two things a user sees in the Sessy app or in Home Assistant and cannot explain: the
        /// battery hopping between "Net zero" and "API", and the battery sitting on a strategy
        /// SessyWeb never asked for. Only ZeroNetHome maps to NOM — Charging, Discharging and
        /// Disabled all go out through the open API — so every mode change across that line is a
        /// strategy rewrite. Counted in BatteriesService, because the log is not visible here.
        /// </summary>
        private void CheckBatteryStrategyStability(List<ConfigurationCheck> checks)
        {
            // A second controller is the more serious of the two: it makes every other check
            // unreliable, because the battery is not doing what the plan says it is doing.
            if (_batteriesService.ForeignStrategyEpisodes > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Something else is changing the battery power strategy",
                    Description = $"{_batteriesService.ForeignStrategyEpisodes} time(s) the battery reported a different " +
                                  $"power strategy than SessyWeb commanded, most recently " +
                                  $"{_batteriesService.LastForeignStrategyAt:dd-MM-yyyy HH:mm} " +
                                  $"({_batteriesService.LastForeignStrategy} instead of {_batteriesService.LastCommandedStrategy}). " +
                                  "SessyWeb only writes the strategy when it differs from what the battery reports, so it " +
                                  "will not fight this — the plan and the hardware simply drift apart. Check whether Home " +
                                  "Assistant, the Sessy app or Charged is also writing the power strategy, and let only one " +
                                  "of them drive. If SessyWeb should not be driving at all, hand control over on the " +
                                  "Settings page instead.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open settings"
                });
            }

            if (_batteriesService.StrategyChurnQuarters > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = $"Battery mode kept changing ({_batteriesService.StrategyChurnQuarters} quarters)",
                    Description = $"Most recently {_batteriesService.LastStrategyChurnAt:dd-MM-yyyy HH:mm}, with " +
                                  $"{_batteriesService.LastStrategyChurnCount} changes inside that one quarter. Only Zero " +
                                  "Net Home runs as the Sessy strategy \"Net zero\"; charging, discharging and off all run " +
                                  "through the open API, so each of those changes rewrites the strategy on the battery. A " +
                                  "few per quarter is normal when a charge finishes or the sun comes up; a long run of them " +
                                  "is not. Look in the log for GUARD_ or PLANNED_MODE_ lines around that time — if there " +
                                  "are none, another system is driving the battery as well.",
                    ActionUrl = "/charginghours",
                    ActionLabel = "Open charging hours"
                });
            }
        }

        private void CheckSolarMeasurement(List<ConfigurationCheck> checks)
        {
            int dropped = _consumptionMonitorService.NegativeConsumptionQuarters;
            var last = _consumptionMonitorService.LastNegativeConsumptionAt;

            if (!_capabilities.HasSolar)
            {
                if (dropped > 0)
                {
                    checks.Add(new ConfigurationCheck
                    {
                        Severity = CheckSeverity.Error,
                        Title = "Solar production is not being measured",
                        Description = $"{dropped} quarter(s) were discarded because the computed consumption was not " +
                                      $"positive, most recently {last:dd-MM-yyyy HH:mm}. Consumption is solar + grid + " +
                                      "battery, and no inverter is configured in PowerSystems, so the solar term is 0. " +
                                      "Every quarter in which the house exports to the grid then comes out negative and " +
                                      "is thrown away — consumption is recorded at night and missing while the panels " +
                                      "produce. Configure the inverter under PowerSystems in appsettings.json. Until " +
                                      "then the daytime figures cannot be measured at all: they are short by exactly " +
                                      "the production that is never read.",
                        ActionUrl = "/consumption",
                        ActionLabel = "Open consumption"
                    });
                }

                return;
            }

            if (!_solarInverterManager.AllAvailable)
            {
                // Name what is actually unreachable. "Inverter" is wrong for the Sessy source and
                // sends the reader off to check an inverter that is not part of the setup.
                bool viaSessy = _solarInverterManager.ActiveInverterServices
                    .Any(s => s.ProviderName == SessyInverterService.SessyProviderName);

                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = viaSessy ? "Batteries not answering, so solar is not measured"
                                     : "Inverter configured but unreachable",
                    Description = (viaSessy
                                      ? "Solar is measured through the Sessy batteries and none of them answered. "
                                      : "An inverter is configured and is not answering. It reports 0 W while offline, so ") +
                                  "consumption samples are skipped during daylight rather than stored short by the " +
                                  "whole solar production. Expect gaps in the consumption history until it is back."
                });

                return;
            }

            if (dropped > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = $"Consumption came out negative ({dropped} quarters)",
                    Description = $"Most recently {last:dd-MM-yyyy HH:mm}. Solar, grid and battery are all being read " +
                                  "but do not add up to a household load, so those quarters were discarded. Check " +
                                  "that every inverter, every P1 meter and every battery in the house is configured — " +
                                  "a device that is producing or exporting outside SessyWeb's view lands in this sum.",
                    ActionUrl = "/consumption",
                    ActionLabel = "Open consumption"
                });
            }
        }

        /// <summary>
        /// The Consumption page reads the table and shows whatever is there, so an empty table looks
        /// exactly like a working page with no data. This says which of the two it is.
        /// </summary>
        private async Task CheckConsumptionHistory(List<ConfigurationCheck> checks)
        {
            var now = _timeZoneService.Now;
            var dayAgo = now.AddDays(-1);

            var latest = await _consumptionDataService.Query(async set =>
                await Task.FromResult(set.Max(c => (DateTime?)c.Time)));

            if (latest == null)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "No consumption recorded",
                    Description = "The consumption table is empty, so the Consumption page has nothing to show and " +
                                  "the planner uses the monthly energy profile from Settings instead of measured " +
                                  "history. Recording needs all three of: a working weather feed, a P1 meter and " +
                                  "reachable batteries — check those first.",
                    ActionUrl = "/consumption",
                    ActionLabel = "Open consumption"
                });
                return;
            }

            var age = now - latest.Value;

            if (age > TimeSpan.FromHours(1))
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Consumption recording has stopped",
                    Description = $"The last consumption record is from {latest.Value:dd-MM-yyyy HH:mm} " +
                                  $"({age.TotalHours:F0} hours ago); a record is expected every 15 minutes. The " +
                                  "weather feed, the P1 meter or a battery is unreachable — a failure in any of " +
                                  "them stops recording.",
                    ActionUrl = "/consumption",
                    ActionLabel = "Open consumption"
                });
                return;
            }

            int recent = await _consumptionDataService.Query(async set =>
                await Task.FromResult(set.Count(c => c.Time >= dayAgo)));

            // 96 quarters a day; well under that means the loop is dropping quarters.
            if (recent < 72)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = $"Consumption history has gaps ({recent} of 96 quarters)",
                    Description = "Fewer records were stored over the last 24 hours than the 96 expected. A quarter " +
                                  "is skipped whenever the P1 meter or a battery cannot be read, or when the " +
                                  "computed consumption comes out at zero.",
                    ActionUrl = "/consumption",
                    ActionLabel = "Open consumption"
                });
                return;
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Info,
                Title = "Consumption recording active",
                Description = $"{recent} quarters stored over the last 24 hours, most recent " +
                              $"{latest.Value:dd-MM-yyyy HH:mm}."
            });
        }

        private Task CheckEneverToken(List<ConfigurationCheck> checks)
        {
            var token = _configuration["Enever:Token"];

            if (string.IsNullOrWhiteSpace(token))
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Enever token not configured",
                    Description = "No live gas price feed available. Add your free Enever token to appsettings.json to enable daily TTF gas price fetching.",
                    ActionUrl = "https://enever.nl/token-aanmaken/",
                    ActionLabel = "Get free token"
                });
            }
            else if (!_epexPricesService.CurrentGasPriceEurPerM3.HasValue)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Enever token configured but no gas price fetched yet",
                    Description = "The token is set but no gas price has been received yet. This is normal on startup — the price is fetched once per day."
                });
            }
            else
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Live gas price active",
                    Description = $"Current TTF gas price (all-in): € {_epexPricesService.CurrentGasPriceEurPerM3.Value:F4}/m³."
                });
            }

            return Task.CompletedTask;
        }

        private async Task CheckTaxesConfiguration(List<ConfigurationCheck> checks)
        {
            var now = _timeZoneService.Now;
            var taxes = await _taxesDataService.GetTaxesForDate(now);

            if (taxes == null)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "No taxes record found",
                    Description = "No applicable Taxes record exists. Energy price calculations will be incorrect. Add a Taxes record in Settings → Taxes.",
                    ActionLabel = "Go to Taxes"
                });
                return;
            }

            // Check gas supplier markup.
            if (taxes.GasSupplierMarkupEurPerM3 == 0.0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Gas supplier markup is €0,00",
                    Description = "The supplier markup (leveranciersopslag) is not configured. The calculated gas price will be lower than your actual bill. " +
                                  "Check your energy contract for the supplier margin and enter it in Settings → Taxes.",
                    ActionLabel = "Go to Taxes"
                });
            }
            else
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Gas taxes configured",
                    Description = $"Energy tax: € {taxes.GasEnergyTaxEurPerM3:F4}/m³, supplier markup: € {taxes.GasSupplierMarkupEurPerM3:F4}/m³, VAT: {taxes.GasValueAddedTaxPct:F1}%."
                });
            }

            // Check electricity taxes completeness.
            if (taxes.EnergyTax == 0.0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Electricity energy tax is €0,00",
                    Description = "The electricity energy tax (energiebelasting) is not configured. Electricity price calculations may be incorrect.",
                    ActionLabel = "Go to Taxes"
                });
            }
        }

        private async Task CheckGasPricesHistory(List<ConfigurationCheck> checks)
        {
            var gasPrices = await _gasPricesDataService.GetAllAsync();

            if (!gasPrices.Any())
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "No gas price history",
                    Description = "No gas prices have been stored yet. The heating-degree-day weighted average cannot be calculated. " +
                                  "Prices are fetched daily — history will build up over time."
                });
            }
            else
            {
                var oldest = gasPrices.Min(g => g.Date);
                var newest = gasPrices.Max(g => g.Date);
                var days = gasPrices.Count;

                if (days < 30)
                {
                    checks.Add(new ConfigurationCheck
                    {
                        Severity = CheckSeverity.Warning,
                        Title = $"Gas price history is short ({days} days)",
                        Description = $"Only {days} days of gas prices stored (since {oldest:dd-MM-yyyy}). " +
                                      "The weighted average will become more accurate as more data accumulates. " +
                                      "A full year gives the most representative seasonal weighting."
                    });
                }
                else
                {
                    checks.Add(new ConfigurationCheck
                    {
                        Severity = CheckSeverity.Info,
                        Title = $"Gas price history: {days} days",
                        Description = $"Gas prices stored from {oldest:dd-MM-yyyy} to {newest:dd-MM-yyyy}. " +
                                      "Heating-degree-day weighted average is active."
                    });
                }
            }
        }

        private void CheckHeatPumpConfiguration(List<ConfigurationCheck> checks)
        {
            if (!_heatPumpConfig.IsConfigured)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Heat pump not configured",
                    Description = "No HeatPumpConfig found in appsettings.json. Heat Pump Savings will not be shown. " +
                                  "If you have a heat pump, add HeatPumpConfig to enable savings tracking."
                });
                return;
            }

            // Check if configured gas price is being used as fallback.
            if (_heatPumpConfig.GasPriceEurPerM3 > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Heat pump configured gas price fallback",
                    Description = $"Configured fallback gas price: € {_heatPumpConfig.GasPriceEurPerM3:F4}/m³. " +
                                  "This is only used when no live Enever data or history is available."
                });
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Info,
                Title = "Heat pump configured",
                Description = $"Annual gas consumption: {_heatPumpConfig.AnnualGasConsumptionM3:F0} m³/year, " +
                              $"installed: {_heatPumpConfig.InstallationDate:dd-MM-yyyy}."
            });
        }

        /// <summary>
        /// How much the planner believes the battery can charge, and on how much evidence.
        ///
        /// The taper is fitted on realized/requested, so it can only use quarters that recorded an
        /// untapered request — a narrow slice, and on 10-08-2026 one that came entirely from a
        /// single heatwave. It predicted 2.3 kW at 80% SOC where the measurements show far more,
        /// and the planner acted on it: 6.6 kWh sold that evening instead of 14.2. The floor now
        /// catches that, but a taper this far off its own measurements is worth saying out loud.
        /// </summary>
        private async Task CheckChargeTaper(List<ConfigurationCheck> checks)
        {
            double nameplateW = _batteryConfig.CurrentValue.TotalRawChargingCapacity;
            if (nameplateW <= 0.0) return;

            var taper = await _throttleAnalysisService.GetChargeTaperAsync().ConfigureAwait(false);
            var floor = await _throttleAnalysisService.GetChargeCapabilityFloorAsync(nameplateW).ConfigureAwait(false);

            if (floor.Samples == 0 && taper.Samples == 0) return;

            const double referenceSoc = 0.8;

            double taperW = taper.Samples > 0 ? taper.Ratio(referenceSoc) * nameplateW : nameplateW;
            double floorW = floor.PowerW(referenceSoc);

            if (floorW > taperW * 1.25)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Charge taper is well below what the batteries have delivered",
                    Description =
                        $"At 80% state of charge the taper predicts {taperW:F0} W while the batteries have " +
                        $"been measured accepting {floorW:F0} W. The measured floor is used instead, so plans " +
                        $"are not affected, but the taper is fitted on only {taper.Samples} points and will " +
                        "stay off until more quarters with a recorded untapered request accumulate."
                });
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Info,
                Title = "Charge model",
                Description =
                    $"Taper fitted on {taper.Samples} envelope points ({taperW:F0} W at 80% SOC). " +
                    $"Measured floor from {floor.Samples} charging quarters covering {floor.CoveredBins} " +
                    $"of 20 SOC bins ({floorW:F0} W at 80% SOC). The planner uses whichever is higher."
            });
        }

        /// <summary>
        /// An investment counts its cost in the payback period whether or not the thing that
        /// produces its savings is configured. Drop HeatPumpConfig and a heat pump worth thousands
        /// keeps its cost and loses its savings, so the payback period on the Statistics page grows
        /// with nothing on screen saying why. Same for a solar investment with no inverter.
        /// </summary>
        private async Task CheckInvestmentsHaveTheirSavingsSource(List<ConfigurationCheck> checks)
        {
            if (!_heatPumpConfig.IsConfigured)
            {
                double heatPumpEur = await NetInvestmentInCategoryAsync(InvestmentCategory.HeatPump);

                if (heatPumpEur > 0)
                {
                    checks.Add(new ConfigurationCheck
                    {
                        Severity = CheckSeverity.Warning,
                        Title = "Heat pump investment counted without its savings",
                        Description = $"€ {heatPumpEur:F0} of heat pump investment is included in the payback " +
                                      "period, but without a HeatPumpConfig section in appsettings.json its " +
                                      "savings count as € 0/year — the payback period shown is too long. " +
                                      "Add HeatPumpConfig, or remove the investment.",
                        ActionLabel = "Go to Investments"
                    });
                }
            }

            if (!_capabilities.HasSolar)
            {
                double solarEur = await NetInvestmentInCategoryAsync(InvestmentCategory.Solar);

                if (solarEur > 0)
                {
                    checks.Add(new ConfigurationCheck
                    {
                        Severity = CheckSeverity.Warning,
                        Title = "Solar investment counted without an inverter",
                        Description = $"€ {solarEur:F0} of solar investment is included in the payback period, " +
                                      "but no inverter is configured in the PowerSystems section of " +
                                      "appsettings.json, so no new production is recorded. Savings stop " +
                                      "accruing while the cost keeps counting.",
                        ActionLabel = "Go to Investments"
                    });
                }
            }
        }

        /// <summary>Net (after subsidy) investment booked to groups of the given category.</summary>
        private async Task<double> NetInvestmentInCategoryAsync(InvestmentCategory category)
        {
            var groups = await _investmentGroupDataService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            var groupIds = groups.Where(g => g.Category == category).Select(g => g.Id).ToHashSet();

            if (groupIds.Count == 0) return 0.0;

            var investments = await _investmentDataService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            return investments
                .Where(i => i.InvestmentGroupId.HasValue && groupIds.Contains(i.InvestmentGroupId.Value))
                .Sum(i => i.AmountEur - i.SubsidyEur);
        }

        /// <summary>
        /// A learned value that lands on its bound is not a measurement, it is the model running
        /// out of room — worth saying out loud rather than applying silently.
        /// </summary>
        private void CheckPlannerLearning(List<ConfigurationCheck> checks)
        {
            var warning = _plannerLearningService.PinnedWarning;
            if (string.IsNullOrWhiteSpace(warning)) return;

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Warning,
                Title = "Learned planner parameter hit its bound",
                Description = $"{warning} The measured value falls outside what the planner accepts, so the " +
                              "bound is being used instead. Check the forecast quality before trusting the plan.",
                ActionUrl = "/settings",
                ActionLabel = "Open settings"
            });
        }

        private void CheckSettingsExtremes(List<ConfigurationCheck> checks)
        {
            var s = _settingsService.Current;
            if (s == null) return;

            // Cycle cost: a high value suppresses all arbitrage; a zero value lets the
            // battery cycle for negligible gain and wear out faster.
            if (_settingsService.CycleCost <= 0.0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Cycle cost is 0",
                    Description = "Cycle cost is € 0.00/kWh. It is derived from the battery investments; " +
                                  "add capacity (Wh) and expected total cycles to each battery investment so " +
                                  "the planner accounts for wear.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open investments"
                });
            }
            else if (_settingsService.CycleCost > 0.20)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Cycle cost very high",
                    Description = $"Cycle cost is € {_settingsService.CycleCost:F2}/kWh. This is high and may block almost all " +
                                  "charging and discharging. It is derived from the battery investments — check the " +
                                  "capacity and expected total cycles entered there.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open investments"
                });
            }

            // Throttle fallback (%). 0 = use the built-in 80% default, so only flag > 0.
            if (s.ThrottleFallbackPct > 0.0 && (s.ThrottleFallbackPct < 50.0 || s.ThrottleFallbackPct > 100.0))
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Throttle fallback out of range",
                    Description = $"Throttle fallback is {s.ThrottleFallbackPct:F0}%. Expected 50–100%. " +
                                  "It caps the power the planner may request until the throttle has been measured.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open settings"
                });
            }

            // Round-trip efficiency fallback (%). 0 = use the built-in 90% default.
            if (s.RoundTripEfficiencyFallbackPct > 0.0 &&
                (s.RoundTripEfficiencyFallbackPct < 50.0 || s.RoundTripEfficiencyFallbackPct > 100.0))
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Round-trip efficiency fallback out of range",
                    Description = $"Round-trip efficiency fallback is {s.RoundTripEfficiencyFallbackPct:F0}%. " +
                                  "Expected 50–100%. A value that is too high makes arbitrage look more profitable than it is.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open settings"
                });
            }

            // Reserve safety surcharge (factor 1.x, shown as % above 100).
            double reservePct = (s.ReserveSafetyFactor - 1.0) * 100.0;
            if (reservePct > 50.0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Reserve safety surcharge very high",
                    Description = $"Reserve safety surcharge is {reservePct:F0}%. The battery will hold a large " +
                                  "reserve and rarely discharge. Typical value is around 10%.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open settings"
                });
            }

            // Night reserve cap (already a whole percentage of capacity).
            if (s.NightReserveCapPct > 80.0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Night reserve cap very high",
                    Description = $"Night reserve cap is {s.NightReserveCapPct:F0}%. The battery keeps most of its " +
                                  "capacity in reserve and barely discharges overnight. Typical value is around 33%.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open settings"
                });
            }

            // Planning horizon: too short loses the evening peak; 0 = no limit (fine).
            if (s.PlanningHorizonHours > 0 && s.PlanningHorizonHours < 12)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Planning horizon very short",
                    Description = $"Planning horizon is {s.PlanningHorizonHours} h. Below ~12 h the planner cannot " +
                                  "see the next price peak and may not save charge for it. Use 0 (no limit), 24 or 36.",
                    ActionUrl = "/settings",
                    ActionLabel = "Open settings"
                });
            }
        }

        private async Task CheckPlanStatus(List<ConfigurationCheck> checks)
        {
            var now = _timeZoneService.Now;
            var plan = await _milpService.GetPlanStatisticsAsync(now, 0.0);

            if (plan.TotalFutureQuarters == 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "No active MILP plan",
                    Description = "No battery plan is currently active. The batteries may be running without optimization. " +
                                  "Check if EPEX prices are available and the service is running."
                });
            }
            else if (plan.IsRestoredFromDb)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Plan restored from database",
                    Description = $"The current plan was restored after a restart. " +
                                  $"It covers {plan.TotalFutureQuarters} future quarters until {plan.PlanHorizon:dd-MM-yyyy HH:mm}. " +
                                  "A fresh plan will be generated when new EPEX prices arrive."
                });
            }
            else
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "MILP plan active",
                    Description = $"Plan generated at {plan.LastBuildTime:dd-MM-yyyy HH:mm}, " +
                                  $"covering {plan.TotalFutureQuarters} quarters ({plan.TotalFutureQuarters / 4.0:F1} hrs). " +
                                  $"Expected profit: € {plan.ExpectedProfitEur:F2}."
                });
            }
        }
    }
}