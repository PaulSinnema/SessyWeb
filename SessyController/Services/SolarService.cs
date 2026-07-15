using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyCommon.Services.Items;
using SessyController.Managers;
using SessyController.Services.InverterServices;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class SolarService : IDisposable
    {
        private IConfiguration _configuration { get; set; }
        private LoggingService<SolarEdgeInverterService> _logger { get; set; }

        private WeatherService _weatherService { get; set; }

        private BatteryContainer _batteryContainer;

        private EPEXPricesService _epexPricesService { get; set; }

        private SolarDataService _solarDataService { get; set; }

        private InverterMeasurementDataService _inverterMeasurementService { get; set; }

        private SolarInverterManager _solarInverterManager { get; set; }

        private SettingsService _settingsService;
        private Settings _settingsConfig;

        private IDisposable? _settingsConfigSubscription { get; set; }


        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        private PowerSystemsConfig _powerSystemsConfig { get; set; }
        private TimeZoneService _timeZoneService { get; set; }

        // Exponentially smoothed performance factor — avoids wild swings from
        // momentary cloud cover. Alpha = 0.3: new observations have 30% weight.
        private double _smoothedPerformanceFactor = 1.0;
        private double _lastLoggedPerformanceFactor = 1.0;
        private DateTime _lastEmaUpdateQuarter = DateTime.MinValue;
        private Dictionary<DateTime, double>? _inverterMeasurementCache;
        private DateTime _lastLoggedPerformanceDate = DateTime.MinValue;
        private DateTime _historicalFactorDate = DateTime.MinValue;
        private const double PerformanceFactorAlpha = 0.2;  // EMA — responds faster to intraday conditions
        private const double PerformanceFactorLogThreshold = 0.05;
        private const int HistoricalFactorLookbackDays = 14;

        // Number of past days to use for global radiation extrapolation.
        private const int ExtrapolationLookbackDays = 14;

        public SolarService(IConfiguration configuration,
                            TimeZoneService timeZoneService,
                            LoggingService<SolarEdgeInverterService> logger,
                            IOptions<PowerSystemsConfig> powerSystemsConfig,
                            BatteryContainer batteryContainer,
                            WeatherService weatherService,
                            EPEXPricesService epexPricesService,
                            SolarDataService solarDataService,
                            InverterMeasurementDataService inverterMeasurementService,
                            SolarInverterManager solarInverterManager,
                            SettingsService settingsService,
                            IServiceScopeFactory serviceScopeFactory)
        {
            _configuration = configuration;
            _timeZoneService = timeZoneService;
            _logger = logger;
            _powerSystemsConfig = powerSystemsConfig.Value;
            _weatherService = weatherService;
            _batteryContainer = batteryContainer;
            _epexPricesService = epexPricesService;
            _solarDataService = solarDataService;
            _inverterMeasurementService = inverterMeasurementService;
            _solarInverterManager = solarInverterManager;
            _serviceScopeFactory = serviceScopeFactory;

            _settingsService = settingsService;
            _settingsConfig = _settingsService.Current;
            _settingsService.SettingsChanged += (s, _) => _settingsConfig = s;
        }

        /// <summary>
        /// Gets the expected solar power from Now for today
        /// </summary>
        /// <summary>
        /// Returns the forecast solar production for the remaining quarters of today,
        /// starting from the current quarter onwards.
        /// Used together with realized production to give an accurate day total in the header.
        /// </summary>
        public double GetRemainingForecastToday(DateTime forDate, DateTime now)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var batteryService = scope.ServiceProvider.GetRequiredService<BatteriesService>();
            var quarterlyInfos = batteryService.GetQuarterlyInfos();
            if (quarterlyInfos == null) return 0.0;

            var nowQuarter = now.DateFloorQuarter();

            return quarterlyInfos
                .Where(qi => qi.Time.Date == forDate.Date && qi.Time >= nowQuarter)
                .Sum(qi => qi.SolarPowerPerQuarterHour);
        }

        public double GetTotalSolarPowerExpected(DateTime forDate)
        {
            using var scope = _serviceScopeFactory.CreateScope();

            var batteryService = scope.ServiceProvider.GetRequiredService<BatteriesService>();
            var quarterlyInfos = batteryService.GetQuarterlyInfos();

            if (quarterlyInfos == null) return 0.0;

            var start = forDate.Date;
            var end = forDate.Date.AddHours(23).AddMinutes(45);
            var now = _timeZoneService.Now;

            var list = quarterlyInfos
                .Where(hi => hi.Time >= start && hi.Time <= end)
                .OrderBy(hi => hi.Time)
                .ToList();

            // For past quarters use the realized solar from InverterMeasurements
            // so the total is not affected by EMA factor changes after the fact.
            // For future quarters use the forecast with performance factor.
            var realizedByQuarter = _inverterMeasurementCache ?? new Dictionary<DateTime, double>();

            double solarPower = 0.0;
            foreach (var qi in list)
            {
                if (qi.Time < now.DateFloorQuarter() && realizedByQuarter.TryGetValue(qi.Time, out var realized))
                    solarPower += realized;
                else
                    solarPower += qi.SolarPowerPerQuarterHour;
            }

            return solarPower;
        }

        /// <summary>
        /// Retrieve the actual measured solar power for a period.
        /// </summary>
        public async Task<double> GetRealizedSolarPower(DateTime start, DateTime end)
        {
            try
            {
                var list = await _inverterMeasurementService.GetList(async (set) =>
                {
                    var result = set
                        .Where(m => m.Time >= start && m.Time <= end)
                        .ToList();

                    return await Task.FromResult(result);
                });

                // Sum kWh per quarter — already in the correct unit.
                return list.Sum(m => m.SolarProductionKWh);
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        public async Task GetExpectedSolarPower(List<QuarterlyInfo> hourlyInfos)
        {
            await GetEstimatesForSolarPower(hourlyInfos);

            await ApplyPerformanceFactor(hourlyInfos, _timeZoneService.Now);

            // Overwrite past quarters with actual measured solar so the chart
            // reflects real production instead of the uncorrected forecast.
            await BackfillActualSolarAsync(hourlyInfos, _timeZoneService.Now);

            AddSmoothedSolarPower(hourlyInfos, 8);
        }

        /// <summary>
        /// Replaces SolarPowerPerQuarterHour for completed past quarters with the
        /// actual measured value from InverterMeasurements, so the chart shows
        /// real production rather than the uncorrected radiation-based forecast.
        /// </summary>
        private async Task BackfillActualSolarAsync(List<QuarterlyInfo> hourlyInfos, DateTime now)
        {
            var from = hourlyInfos.Min(q => q.Time);
            var to = now.DateFloorQuarter();

            if (to <= from) return;

            var measurements = await _inverterMeasurementService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= from && m.Time < to)
                    .ToList()));

            var measuredByQuarter = measurements
                .GroupBy(m => m.Time)
                .ToDictionary(g => g.Key, g => g.Sum(m => m.SolarProductionKWh));

            foreach (var qi in hourlyInfos.Where(q => q.Time < to))
            {
                if (measuredByQuarter.TryGetValue(qi.Time, out var kWh))
                    qi.SolarPowerPerQuarterHour = kWh;
            }
        }

        private void AddSmoothedSolarPower(List<QuarterlyInfo> hourlyInfos, int windowSize = 6)
        {
            if (hourlyInfos == null || hourlyInfos.Count == 0) return;

            for (int i = 0; i < hourlyInfos.Count; i++)
            {
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(hourlyInfos.Count - 1, i + windowSize / 2);

                var range = hourlyInfos.Skip(start).Take(end - start + 1);

                double average = range.Count() > 0 ? range.Average(h => h.SolarPowerPerQuarterHour) : 0.0;

                hourlyInfos[i].SmoothedSolarPower = average;
            }
        }

        /// <summary>
        /// Calculate the solar power using:
        /// - The solar panel information from the settings.
        ///     - Solar panels
        ///     - Position (longitude, latitude)
        /// - Negative prices assessment
        /// For quarters beyond the WeerOnline 24h forecast window, global radiation
        /// is extrapolated from historical averages for the same hour and day of week.
        /// </summary>
        private async Task GetEstimatesForSolarPower(List<QuarterlyInfo> hourlyInfos)
        {
            if (_weatherService.IsInitialized() && _epexPricesService.IsInitialized())
            {
                var startDate = hourlyInfos.Min(hi => hi.Time);
                var endDate = hourlyInfos.Max(hi => hi.Time);

                var data = await _solarDataService.GetList(async (set) =>
                {
                    var result = set
                        .Where(sd => sd.Time >= startDate && sd.Time <= endDate)
                        .OrderBy(sd => sd.Time)
                        .ToList();

                    return await Task.FromResult(result);
                });

                // Build a lookup of quarters that have weather forecast data.
                var coveredTimes = new HashSet<DateTime>(
                    data.Where(sd => sd.Time.HasValue).Select(sd => sd.Time!.Value));

                if (data != null)
                {
                    foreach (SolarData solarData in data)
                    {
                        var currentHourlyInfo = hourlyInfos.Where(hi => hi.Time == solarData.Time).FirstOrDefault();

                        if (currentHourlyInfo != null)
                        {
                            currentHourlyInfo.SolarGlobalRadiation = solarData.GlobalRadiation;

                            currentHourlyInfo.SolarPowerPerQuarterHour = 0.0;

                            foreach (var config in _powerSystemsConfig.Endpoints.Values)
                            {
                                foreach (var id in config.Keys)
                                {
                                    CalculateSolarPerArray(solarData, currentHourlyInfo, config, id);
                                }
                            }

                            AddSolarPowerToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 15);
                            AddSolarPowerToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 30);
                            AddSolarPowerToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 45);
                        }
                    }
                }

                // Extrapolate solar power for quarters not covered by the 24h forecast.
                // Uses the average global radiation of the same hour from the past
                // ExtrapolationLookbackDays days as a proxy for expected radiation.
                var uncoveredInfos = hourlyInfos
                    .Where(hi => !coveredTimes.Contains(hi.Time) && hi.SolarPowerPerQuarterHour == 0.0)
                    .ToList();

                if (uncoveredInfos.Count > 0)
                {
                    await ExtrapolateSolarPowerAsync(hourlyInfos, uncoveredInfos);
                }
            }
        }

        /// <summary>
        /// Extrapolates global radiation and solar power for quarters that fall outside
        /// the WeerOnline 24h forecast window.
        ///
        /// Strategy: for each uncovered quarter, look up all historical SolarData records
        /// from the past ExtrapolationLookbackDays days that share the same hour of day.
        /// The average global radiation of those records is used as the estimate.
        /// Solar power is then calculated using the existing panel geometry methods.
        /// </summary>
        private async Task ExtrapolateSolarPowerAsync(
            List<QuarterlyInfo> allInfos,
            List<QuarterlyInfo> uncoveredInfos)
        {
            var now = _timeZoneService.Now;
            var lookbackStart = now.Date.AddDays(-ExtrapolationLookbackDays);

            // Fetch all historical radiation records for the lookback window.
            var historicalData = await _solarDataService.GetList(async (set) =>
            {
                var result = set
                    .Where(sd => sd.Time >= lookbackStart && sd.Time < now.Date)
                    .OrderBy(sd => sd.Time)
                    .ToList();

                return await Task.FromResult(result);
            });

            // Group by hour of day for fast lookup.
            var radiationByHour = historicalData
                .Where(sd => sd.Time.HasValue)
                .GroupBy(sd => sd.Time!.Value.Hour)
                .ToDictionary(
                    g => g.Key,
                    g => g.Average(sd => sd.GlobalRadiation));

            int extrapolatedCount = 0;

            foreach (var qi in uncoveredInfos)
            {
                int hour = qi.Time.Hour;

                if (!radiationByHour.TryGetValue(hour, out double estimatedRadiation))
                    continue; // No historical data for this hour — leave at zero (night).

                // Create a synthetic SolarData record with the estimated radiation.
                var syntheticSolarData = new SolarData
                {
                    Time = qi.Time,
                    GlobalRadiation = estimatedRadiation
                };

                qi.SolarGlobalRadiation = estimatedRadiation;
                qi.SolarPowerPerQuarterHour = 0.0;

                foreach (var config in _powerSystemsConfig.Endpoints.Values)
                {
                    foreach (var id in config.Keys)
                    {
                        CalculateSolarPerArray(syntheticSolarData, qi, config, id);
                    }
                }

                // Propagate to the other three quarter-hour slots within the same hour.
                AddSolarPowerToHourlyInfosFor15MinuteResolution(allInfos, qi, 15);
                AddSolarPowerToHourlyInfosFor15MinuteResolution(allInfos, qi, 30);
                AddSolarPowerToHourlyInfosFor15MinuteResolution(allInfos, qi, 45);

                extrapolatedCount++;
            }

            _logger.LogInformation($"Solar extrapolation: {extrapolatedCount} quarters estimated from {ExtrapolationLookbackDays}-day average radiation.");
        }

        /// <summary>
        /// Calculates the average performance factor over the last N days by comparing
        /// realized solar production (InverterMeasurements) to radiation-based forecast
        /// (SolarData). Uses median over 30 days to filter outliers.
        /// Returns 1.0 when insufficient data is available.
        /// </summary>
        /// <summary>
        /// Calculates the historical performance factor from the last N days by comparing
        /// realized solar (InverterMeasurements) to the forecast computed from SolarData
        /// + panel configuration — only for quarters where both are available and radiation > 0.
        /// </summary>
        private async Task<double> CalculateHistoricalPerformanceFactorAsync(DateTime now)
        {
            var from = now.Date.AddDays(-HistoricalFactorLookbackDays);
            var to = now.Date;

            // Load SolarData with radiation > 0.
            var solarDataList = await _solarDataService.GetList(async set =>
                await Task.FromResult(set
                    .Where(s => s.Time.HasValue && s.Time >= from && s.Time < to && s.GlobalRadiation > 0)
                    .ToList()));

            if (!solarDataList.Any())
                return 1.0;

            // Load realized solar for the same period.
            var realizedList = await _inverterMeasurementService.GetList(async set =>
                await Task.FromResult(set
                    .Where(m => m.Time >= from && m.Time < to)
                    .ToList()));

            // Group realized measurements by hour (floor to whole hour) and sum all quarters.
            // SolarData has hourly resolution; InverterMeasurements has 15-minute resolution.
            var realizedByHour = realizedList
                .GroupBy(m => m.Time.AddMinutes(-(m.Time.Minute % 60)).AddSeconds(-m.Time.Second))
                .ToDictionary(g => g.Key, g => g.Sum(m => m.SolarProductionKWh));

            var endpointConfigs = _powerSystemsConfig?.Endpoints;
            if (endpointConfigs == null || !endpointConfigs.Any())
                return 1.0;

            var latitude = _settingsConfig.Latitude;
            var longitude = _settingsConfig.Longitude;
            double totalForecast = 0.0;
            double totalRealized = 0.0;

            foreach (var solarData in solarDataList)
            {
                if (!solarData.Time.HasValue) continue;
                if (!realizedByHour.TryGetValue(solarData.Time.Value, out var realized)) continue;

                CalculateSolarPosition(solarData.Time.Value.AddMinutes(30), latitude, longitude,
                    out double solarAltitude, out double solarAzimuth);

                if (solarAltitude <= 0) continue;

                double forecast = 0.0;
                foreach (var config in endpointConfigs.Values)
                {
                    foreach (var endpoint in config.Values)
                    {
                        if (endpoint.SolarPanels == null || !endpoint.SolarPanels.Any()) continue;
                        foreach (PhotoVoltaic panel in endpoint.SolarPanels.Values)
                        {
                            double? sf = GetSolarFactor(solarAzimuth, solarAltitude, panel.Orientation, panel.Tilt);
                            // CalculateSolarPowerPerQuarterHour returns kWh per quarter; multiply by 4 for hourly total.
                            forecast += CalculateSolarPowerPerQuarterHour(
                                solarData.GlobalRadiation, sf, panel, solarAltitude) * 4.0;
                        }
                    }
                }

                totalForecast += forecast;
                totalRealized += realized;
            }

            if (totalForecast <= 1.0)
                return 1.0;

            double factor = totalRealized / totalForecast;
            _logger.LogInformation($"Solar: Historical performance factor = {factor:F2} " +
                $"(Realized={totalRealized:F1} kWh, Forecast={totalForecast:F1} kWh, {HistoricalFactorLookbackDays} days)");
            return Math.Max(0.2, Math.Min(3.0, factor));
        }


        private async Task ApplyPerformanceFactor(List<QuarterlyInfo> hourlyInfos, DateTime now)
        {
            // Skip performance factor correction when no inverter is available.
            // Without live solar data the forecast cannot be corrected reliably.
            if (!_solarInverterManager.IsAvailable && !_solarInverterManager.ActiveInverterServices.Any(s => s.SupportsFallback))
            {
                _logger.LogInformation("Solar: All inverters offline with no fallback — skipping performance factor, using forecast as-is.");
                return;
            }

            // On first run (after restart) or at the start of each new day, reset the EMA
            // to the historical average factor calculated from the last 30 days.
            // This gives a realistic starting point immediately after a restart,
            // rather than waiting for the EMA to converge from 1.0.
            bool firstRun = _historicalFactorDate == DateTime.MinValue;
            if (firstRun || now.Date != _historicalFactorDate)
            {
                var historicalFactor = await CalculateHistoricalPerformanceFactorAsync(now).ConfigureAwait(false);
                _smoothedPerformanceFactor = historicalFactor;
                _historicalFactorDate = now.Date;
                _logger.LogInformation($"Solar: Performance factor initialised to {historicalFactor:F2} " +
                    $"({(firstRun ? "first run after restart" : "new day")}, last {HistoricalFactorLookbackDays} days).");
            }

            // Only quarter hours of today
            var todayInfos = hourlyInfos
                .Where(q => q.Time.Date == now.Date)
                .ToList();

            var pastInfos = todayInfos
                .Where(q => q.Time < now)
                .ToList();

            if (pastInfos.Count == 0)
            {
                _logger.LogInformation("Solar: No past quarter hours found — performance factor not applied.");
                return;
            }

            // Build cache of realized solar per quarter for use in GetTotalSolarPowerExpected.
            // This prevents factor changes from retroactively altering past quarter values.
            var todayMeasurements = await _inverterMeasurementService.GetList(async set =>
            {
                var result = set
                    .Where(m => m.Time >= now.Date && m.Time < now.DateFloorQuarter())
                    .ToList();
                return await Task.FromResult(result);
            }).ConfigureAwait(false);

            _inverterMeasurementCache = todayMeasurements
                .GroupBy(m => m.Time)
                .ToDictionary(g => g.Key, g => g.First().SolarProductionKWh * 4.0); // kWh → kW per quarter

            // Only the stable historical factor (last N days) is used. The intraday
            // realized/forecast ratio was removed: it reacted to weather forecast errors
            // (e.g. radiation predicted too low on a sunny day), producing extreme raw
            // factors that inflated the whole forward forecast. The historical factor
            // already captures systematic panel performance without that day-noise.
            var factor = _smoothedPerformanceFactor;

            bool newDay = now.Date != _lastLoggedPerformanceDate;
#if DEBUG
            bool significantChange = true;
#else
            bool significantChange = Math.Abs(factor - _lastLoggedPerformanceFactor) >= PerformanceFactorLogThreshold;
#endif
            if (newDay || significantChange)
            {
                _logger.LogInformation($"Solar: Performance factor applied: {factor:F2} (historical, last {HistoricalFactorLookbackDays} days)");
                _lastLoggedPerformanceFactor = factor;
                _lastLoggedPerformanceDate = now.Date;
            }

            // Adjust all quarters from now onwards with the performance factor.
            // This corrects not just today's remaining forecast but also extrapolated
            // future days, which are based on historical averages and may be too
            // optimistic on cloudy days.
            foreach (var q in hourlyInfos.Where(q => q.Time >= now))
            {
                q.SolarPowerPerQuarterHour *= factor;
            }
        }

        private void CalculateSolarPerArray(SolarData solarData, QuarterlyInfo currentHourlyInfo, Dictionary<string, SessyCommon.Configurations.Endpoint> config, string id)
        {
            var endpoint = config[id];

            var longitude = _settingsConfig.Longitude;
            var latitude = _settingsConfig.Latitude;

            double solarAltitude;
            double solarAzimuth;

            CalculateSolarPosition(currentHourlyInfo.Time.AddMinutes(30), latitude, longitude, out solarAltitude, out solarAzimuth);

            if (endpoint.SolarPanels == null || !endpoint.SolarPanels.Any()) return;

            foreach (PhotoVoltaic solarPanel in endpoint.SolarPanels.Values)
            {
                double? solarFactor = GetSolarFactor(solarAzimuth, solarAltitude, solarPanel.Orientation, solarPanel.Tilt);
                currentHourlyInfo.SolarPowerPerQuarterHour += CalculateSolarPowerPerQuarterHour(solarData.GlobalRadiation, solarFactor, solarPanel, solarAltitude);
            }
        }

        /// <summary>
        /// Applies one EMA step. Internal for unit testing.
        /// newEma = alpha * rawFactor + (1 - alpha) * currentEma
        /// </summary>
        internal static double UpdateEma(double currentEma, double rawFactor, double alpha)
            => alpha * rawFactor + (1.0 - alpha) * currentEma;


        /// <summary>
        /// Currently ENTSO-E does not have the 15 minutes resolution active (from 1 October 2025). So we need
        /// to add fake data for the missing quarters.
        /// </summary>
        private void AddSolarPowerToHourlyInfosFor15MinuteResolution(List<QuarterlyInfo> hourlyInfos, QuarterlyInfo? lastHourlyInfo, int minutes)
        {
            if (lastHourlyInfo != null)
            {
                var date = lastHourlyInfo.Time.DateHour().AddMinutes(minutes);
                var quarterlyInfo = hourlyInfos.Where(hi => hi.Time == date).FirstOrDefault();

                if (quarterlyInfo != null)
                {
                    quarterlyInfo.SolarGlobalRadiation = lastHourlyInfo.SolarGlobalRadiation;
                    quarterlyInfo.SolarPowerPerQuarterHour = lastHourlyInfo.SolarPowerPerQuarterHour;
                }
            }
        }

        public double CalculateSolarPowerPerQuarterHour(double globalRadiation, double? solarFactor, PhotoVoltaic solarPanel, double solarAltitude)
        {
            double totalPeakPower = solarPanel.PeakPowerForArray;

            double altitudeFactor = (solarAltitude > 10) ? 1.0 : Math.Max(0, solarAltitude / 10.0);

            double powerkWatt = globalRadiation * (totalPeakPower / 1000.0) * (solarFactor ?? 0.0) * altitudeFactor / 1000.0 / 4.0; // kW per quarter hour

            return powerkWatt;
        }

        private void CalculateSolarPosition(DateTime dateTime, double latitude, double longitude, out double altitude, out double azimuth)
        {
            // Determine timezone offset — use configured timezone, fallback to UTC+1/+2.
            TimeZoneInfo? tz = null;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(_settingsConfig.TimeZone ?? "Europe/Amsterdam"); } catch { }
            if (tz == null)
                try { tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); } catch { }
            TimeSpan offset = tz != null ? tz.GetUtcOffset(dateTime) : TimeSpan.FromHours(1);
            int timezoneOffset = (int)offset.TotalHours;

            int dayOfYear = dateTime.DayOfYear;

            double declination = 23.45 * Math.Sin((2 * Math.PI / 365) * (dayOfYear - 81));

            double minutesOfDay = dateTime.TimeOfDay.TotalMinutes;

            double solarTimeOffset = 4 * (longitude - 15 * timezoneOffset);
            double trueSolarTime = minutesOfDay + solarTimeOffset;

            double hourAngle = (trueSolarTime / 4.0) - 180.0;

            double latRad = latitude * Math.PI / 180.0;
            double decRad = declination * Math.PI / 180.0;
            double haRad = hourAngle * Math.PI / 180.0;

            altitude = Math.Asin(
                Math.Sin(latRad) * Math.Sin(decRad) +
                Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad))
                * 180.0 / Math.PI;

            double cosAzimuth = (
                Math.Sin(decRad) -
                Math.Sin(latRad) * Math.Sin(altitude * Math.PI / 180.0))
                / (Math.Cos(latRad) * Math.Cos(altitude * Math.PI / 180.0));

            azimuth = Math.Acos(Math.Max(-1, Math.Min(1, cosAzimuth))) * 180.0 / Math.PI;

            if (hourAngle > 0)
                azimuth = 360 - azimuth;
        }

        /// <summary>
        /// Calculates the solar factor for a photovoltaic panel based on the sun's azimuth and altitude,
        /// the panel's orientation and tilt, and applies a correction factor from the settings.
        /// The factor represents the effective fraction of solar radiation received by the panel.
        /// </summary>
        private double? GetSolarFactor(double solarAzimuth, double solarAltitude, double orientationDegrees, double tilt)
        {
            double angleDifference = Math.Abs(orientationDegrees - solarAzimuth);
            if (angleDifference > 180) angleDifference = 360 - angleDifference;

            // Calculate Radials
            double alphaRad = solarAltitude * Math.PI / 180;
            double betaRad = tilt * Math.PI / 180;
            double gammaRad = angleDifference * Math.PI / 180;

            double cosThetaI = Math.Sin(alphaRad) * Math.Cos(betaRad) + Math.Cos(alphaRad) * Math.Sin(betaRad) * Math.Cos(gammaRad);

            // Sun behind solar panel — factor becomes zero.
            var factor = Math.Max(0, cosThetaI);

            // SolarCorrection is no longer applied here — the historical performance
            // factor calculated from InverterMeasurements vs SolarData replaces it.
            return factor;
        }

        private bool isDisposed = false;

        public void Dispose()
        {
            if (!isDisposed)
            {
                isDisposed = true;
            }
        }
    }
}