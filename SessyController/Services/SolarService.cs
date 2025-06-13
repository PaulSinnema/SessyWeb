using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    public class SolarService : IDisposable
    {
        private IConfiguration _configuration { get; set; }
        private LoggingService<SolarEdgeService> _logger { get; set; }

        private WeatherService _weatherService { get; set; }

        private DayAheadMarketService _dayAheadMarketService { get; set; }

        private SolarDataService _solarDataService { get; set; }

        private SettingsConfig _settingsConfig { get; set; }

        private IDisposable? _settingsConfigSubscription { get; set; }

        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }

        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        private PowerSystemsConfig _powerSystemsConfig { get; set; }
        private TimeZoneService _timeZoneService { get; set; }

        public SolarService(IConfiguration configuration,
                            TimeZoneService timeZoneService,
                            LoggingService<SolarEdgeService> logger,
                            IOptions<PowerSystemsConfig> powerSystemsConfig,
                            WeatherService weatherService,
                            DayAheadMarketService dayAheadMarketService,
                            SolarDataService solarDataService,
                            IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                            IServiceScopeFactory serviceScopeFactory)
        {
            _configuration = configuration;
            _timeZoneService = timeZoneService;
            _logger = logger;
            _powerSystemsConfig = powerSystemsConfig.Value;
            _weatherService = weatherService;
            _dayAheadMarketService = dayAheadMarketService;
            _solarDataService = solarDataService;
            _settingsConfigMonitor = settingsConfigMonitor;
            _serviceScopeFactory = serviceScopeFactory;

            _settingsConfig = _settingsConfigMonitor.CurrentValue;
            _settingsConfigSubscription = _settingsConfigMonitor.OnChange((settings) => _settingsConfig = settings);
        }

        /// <summary>
        /// Gets the expected solar power from Now for today
        /// </summary>
        public double GetTotalSolarPowerExpected(DateTime forDate)
        {
            using var scope = _serviceScopeFactory.CreateScope();

            var batteryService = scope.ServiceProvider.GetRequiredService<BatteriesService>();

            var hourlyInfos = batteryService.GetHourlyInfos();

            if (hourlyInfos != null)
            {
                var solarPower = 0.0;
                var start = forDate.Date;
                var end = forDate.Date.AddHours(23).AddMinutes(45);

                var list = hourlyInfos
                    .Where(hi => hi.Time >= start && hi.Time <= end)
                    .OrderBy(hi => hi.Time)
                    .ToList();

                foreach (var hourlyInfo in list)
                {
                    solarPower += hourlyInfo.SolarPower;
                }

                return solarPower;
            }

            return 0.0;
        }

        public void GetExpectedSolarPower(List<HourlyInfo> hourlyInfos)
        {
            GetEstimatesForSolarPower(hourlyInfos);

            AddSmoothedSolarPower(hourlyInfos, 8);
        }

        private void AddSmoothedSolarPower(List<HourlyInfo> hourlyInfos, int windowSize = 6)
        {
            if (hourlyInfos == null || hourlyInfos.Count == 0) return;

            for (int i = 0; i < hourlyInfos.Count; i++)
            {
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(hourlyInfos.Count - 1, i + windowSize / 2);

                var range = hourlyInfos.Skip(start).Take(end - start + 1);

                double average = range.Count() > 0 ? range.Average(h => h.SolarPower) : 0.0;

                hourlyInfos[i].SmoothedSolarPower = average;
            }
        }

        /// <summary>
        /// Calculate the solar power using:
        /// - The solar panel information from the settings.
        ///     - Solar panels
        ///     - Position (longitude, latitude)
        /// - Negative prices assessment
        /// </summary>
        private void GetEstimatesForSolarPower(List<HourlyInfo> hourlyInfos)
        {
            if (_weatherService.Initialized && _dayAheadMarketService.PricesAvailable)
            {
                var startDate = hourlyInfos.Min(hi => hi.Time);
                var endDate = hourlyInfos.Max(hi => hi.Time);

                var data = _solarDataService.GetList((set) =>
                {
                    return set
                        .Where(sd => sd.Time >= startDate && sd.Time <= endDate)
                        .OrderBy(sd => sd.Time)
                        .ToList();
                });

                if (data != null)
                {

                    foreach (SolarData solarData in data)
                    {
                        var currentHourlyInfo = hourlyInfos.Where(hi => hi.Time == solarData.Time).FirstOrDefault();

                        if (currentHourlyInfo != null)
                        {
                            currentHourlyInfo.SolarGlobalRadiation = solarData.GlobalRadiation;

                            currentHourlyInfo.SolarPower = 0.0;

                            if (SolarSystemRunning(currentHourlyInfo))
                            {
                                foreach (var config in _powerSystemsConfig.Endpoints.Values)
                                {
                                    foreach (var id in config.Keys)
                                    {
                                        var endpoint = config[id];

                                        var longitude = endpoint.Longitude;
                                        var latitude = endpoint.Latitude;

                                        double solarAltitude;
                                        double solarAzimuth;

                                        CalculateSolarPosition(solarData.Time!.Value.Hour, latitude, longitude, endpoint.TimeZoneOffset, out solarAltitude, out solarAzimuth);

                                        foreach (PhotoVoltaic solarPanel in endpoint.SolarPanels.Values)
                                        {
                                            double solarFactor = GetSolarFactor(solarAzimuth, solarAltitude, solarPanel.Orientation, solarPanel.Tilt);

                                            currentHourlyInfo.SolarPower += CalculateSolarPower(solarData.GlobalRadiation, solarFactor, solarPanel, solarAltitude);
                                        }
                                    }
                                }
                            }

                            AddSolarPowerToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 15);
                            AddSolarPowerToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 30);
                            AddSolarPowerToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 45);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the inverter does not shut down due to negative prices.
        /// </summary>
        private bool SolarSystemRunning(HourlyInfo currentHourlyInfo)
        {
            if (!_settingsConfig.SolarSystemShutsDownDuringNegativePrices)
                return true;

            if (currentHourlyInfo.SellingPriceIsPositive)
                return true;

            return false;
        }

        /// <summary>
        /// Currently ENTSO-E does not have the 15 minutes resolution active (from 11 juni 2025). So we need
        /// to add fake data for the missing quarters.
        /// </summary>
        private void AddSolarPowerToHourlyInfosFor15MinuteResolution(List<HourlyInfo> hourlyInfos, HourlyInfo? lastHourlyInfo, int v)
        {
            if (lastHourlyInfo != null)
            {
                var date = lastHourlyInfo.Time.DateHour().AddMinutes(v);
                var hourlyInfo = hourlyInfos.Where(hi => hi.Time == date).FirstOrDefault();

                if (hourlyInfo != null)
                {
                    hourlyInfo.SolarGlobalRadiation = lastHourlyInfo.SolarGlobalRadiation;
                    hourlyInfo.SolarPower = lastHourlyInfo.SolarPower;
                }
            }
        }

        public double CalculateSolarPower(double globalRadiation, double solarFactor, PhotoVoltaic solarPanel, double solarAltitude)
        {
            double totalPeakPower = solarPanel.HighestDailySolarProduction;

            double altitudeFactor = (solarAltitude > 10) ? 1.0 : Math.Max(0, solarAltitude / 10.0);

            double powerkWatt = globalRadiation * (totalPeakPower / 1000) * solarFactor * altitudeFactor / 1000;

            return powerkWatt / 4; // kW per quarter hour
        }

        private void CalculateSolarPosition(int hour, double latitude, double longitude, int timezoneOffset, out double altitude, out double azimuth)
        {
            int dayOfYear = _timeZoneService.Now.DayOfYear; // Dynamic day of the year.
            double declination = 23.45 * Math.Sin((2 * Math.PI / 365) * (dayOfYear - 81)); // Correct declination angle

            // Calculate solar angle and longituge compensation.
            double solarTimeOffset = 4 * (longitude - 15 * timezoneOffset); // 4 minutes per degree
            double trueSolarTime = hour * 60 + solarTimeOffset;
            double hourAngle = (trueSolarTime / 4.0) - 180.0;

            double latRad = latitude * Math.PI / 180.0;
            double decRad = declination * Math.PI / 180.0;
            double haRad = hourAngle * Math.PI / 180.0;

            // Calculate altitude.
            altitude = Math.Asin(Math.Sin(latRad) * Math.Sin(decRad) + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad)) * 180.0 / Math.PI;

            double cosAzimuth = (Math.Sin(decRad) - Math.Sin(latRad) * Math.Sin(altitude * Math.PI / 180)) / (Math.Cos(latRad) * Math.Cos(altitude * Math.PI / 180));
            azimuth = Math.Acos(Math.Max(-1, Math.Min(1, cosAzimuth))) * 180.0 / Math.PI; // Prevent NaN errors

            if (hourAngle > 0) azimuth = 360 - azimuth; // Correction for afternoon.

            _logger.LogInformation($"Hour: {hour}, Solar Altitude: {altitude:F2}, Solar Azimuth: {azimuth:F2}, Hour Angle: {hourAngle:F2}");
        }

        private double GetSolarFactor(double solarAzimuth, double solarAltitude, double orientationDegrees, double tilt)
        {
            double panelAzimuth = orientationDegrees;
            double angleDifference = Math.Abs(panelAzimuth - solarAzimuth);

            if (angleDifference > 180) angleDifference = 360 - angleDifference;

            // Account for panel tilt
            double tiltFactor = Math.Cos((90 - solarAltitude) * Math.PI / 180) * Math.Cos(tilt * Math.PI / 180);

            var factor = Math.Max(0, Math.Cos(angleDifference * Math.PI / 180) * tiltFactor);

            return factor * _settingsConfig.SolarCorrection;
        }

        private bool isDisposed = false;

        public void Dispose()
        {
            if (!isDisposed)
            {
                _settingsConfigSubscription.Dispose();
                isDisposed = true;
            }
        }
    }
}
