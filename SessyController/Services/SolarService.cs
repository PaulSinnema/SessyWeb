using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using System.Globalization;
using static SessyController.Services.WeatherService;

namespace SessyController.Services
{
    public class SolarService : IDisposable
    {
        private IConfiguration _configuration { get; set; }
        private LoggingService<SolarEdgeService> _logger { get; set; }

        private WeatherService _weatherService { get; set; }

        private SolarDataService _solarDataService { get; set; }

        private SettingsConfig _settingsConfig { get; set; }

        private IDisposable? _settingsConfigSubscription { get; set; }

        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }

        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        private PowerSystemsConfig _powerSystemsConfig { get; set; }
        private TimeZoneService _timeZoneService { get; set; }

        Dictionary<string, double> orientations = new Dictionary<string, double>
            {
                { "south", 180 },
                { "east", 90 },
                { "west", 270 },
                { "north", 0 },
                { "southwest", 225 },
                { "southeast", 135 },
                { "northeast", 45 },
                { "northwest", 315 }
            };

        public SolarService(IConfiguration configuration,
                            TimeZoneService timeZoneService,
                            LoggingService<SolarEdgeService> logger,
                            IOptions<PowerSystemsConfig> powerSystemsConfig,
                            WeatherService weatherService,
                            SolarDataService solarDataService,
                            IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                            IServiceScopeFactory serviceScopeFactory)
        {
            _configuration = configuration;
            _timeZoneService = timeZoneService;
            _logger = logger;
            _powerSystemsConfig = powerSystemsConfig.Value;
            _weatherService = weatherService;
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
            if (_weatherService.Initialized)
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
                    foreach (SolarData? solarData in data)
                    {
                        var currentHourlyInfo = hourlyInfos.Where(hi => hi.Time == solarData.Time).FirstOrDefault();

                        if (currentHourlyInfo != null)
                        {
                            currentHourlyInfo.SolarPower = 0.0;

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

                                        currentHourlyInfo.SolarPower += CalculateSolarPower(solarData.GlobalRadiation, solarFactor, solarPanel, solarAltitude) / 4; // per quarter hour

                                        currentHourlyInfo.SolarGlobalRadiation = solarData.GlobalRadiation;
                                    }
                                }
                            }

                            AddToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 15);
                            AddToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 30);
                            AddToHourlyInfosFor15MinuteResolution(hourlyInfos, currentHourlyInfo, 45);
                        }
                    }
                }
            }
        }

        private void AddToHourlyInfosFor15MinuteResolution(List<HourlyInfo> hourlyInfos, HourlyInfo? lastHourlyInfo, int v)
        {
            if(lastHourlyInfo != null)
            {
                var date = lastHourlyInfo.Time.AddMinutes(v);
                var hourlyInfo = hourlyInfos.Where(hi => hi.Time == date).FirstOrDefault();
                hourlyInfo.SolarGlobalRadiation = lastHourlyInfo.SolarGlobalRadiation;
                hourlyInfo.SolarPower = lastHourlyInfo.SolarPower;
            }
        }

        public double CalculateSolarPower(double globalRadiation, double solarFactor, PhotoVoltaic solarPanel, double solarAltitude)
        {
            double totalPeakPower = solarPanel.HighestDailySolarProduction;

            double altitudeFactor = (solarAltitude > 10) ? 1.0 : Math.Max(0, solarAltitude / 10.0);

            double powerkWatt = globalRadiation * ( totalPeakPower / 1000) * solarFactor * altitudeFactor / 1000;

            return powerkWatt; // kWh
        }

        private void CalculateSolarPosition(int hour, double latitude, double longitude, int timezoneOffset, out double altitude, out double azimuth)
        {
            int dayOfYear = _timeZoneService.Now.DayOfYear; // Dynamische dag van het jaar
            double declination = 23.45 * Math.Sin((2 * Math.PI / 365) * (dayOfYear - 81)); // Juiste declinatiehoek

            // Correcte zonne-uurhoekberekening met lengtegraadcompensatie
            double solarTimeOffset = 4 * (longitude - 15 * timezoneOffset); // 4 minuten per graad
            double trueSolarTime = hour * 60 + solarTimeOffset;
            double hourAngle = (trueSolarTime / 4.0) - 180.0;

            double latRad = latitude * Math.PI / 180.0;
            double decRad = declination * Math.PI / 180.0;
            double haRad = hourAngle * Math.PI / 180.0;

            // Berekening zonnehoogte (altitude)
            altitude = Math.Asin(Math.Sin(latRad) * Math.Sin(decRad) + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad)) * 180.0 / Math.PI;

            // Verbeterde berekening van azimut
            double cosAzimuth = (Math.Sin(decRad) - Math.Sin(latRad) * Math.Sin(altitude * Math.PI / 180)) / (Math.Cos(latRad) * Math.Cos(altitude * Math.PI / 180));
            azimuth = Math.Acos(Math.Max(-1, Math.Min(1, cosAzimuth))) * 180.0 / Math.PI; // Voorkom NaN fouten

            if (hourAngle > 0) azimuth = 360 - azimuth; // Correctie voor middag

            // Debug logging om waarden te controleren
            _logger.LogInformation($"Hour: {hour}, Solar Altitude: {altitude:F2}, Solar Azimuth: {azimuth:F2}, Hour Angle: {hourAngle:F2}");
        }

        private double GetSolarFactor(double solarAzimuth, double solarAltitude, string orientation, double tilt)
        {
            var key = orientation.ToLower();

            if (!orientations.ContainsKey(key))
                throw new InvalidOperationException($"Unknown orientation: {orientation}");

            double panelAzimuth = orientations[key];
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
