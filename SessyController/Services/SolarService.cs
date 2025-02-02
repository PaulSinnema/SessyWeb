using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using System.Globalization;
using static SessyController.Services.WeatherService;

namespace SessyController.Services
{
    public class SolarService
    {
        private IConfiguration _configuration { get; set; }
        private LoggingService<SolarEdgeService> _logger { get; set; }

        private WeatherService _weatherService { get; set; }

        private PowerSystemsConfig _powerSystemsConfig { get; set; }
        private TimeZoneService _timeZoneService { get; set; }

        private ModelContext _modelContext { get; set; }

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
                                      ModelContext modelContext,
                                      WeatherService weatherService)
        {
            _configuration = configuration;
            _logger = logger;
            _powerSystemsConfig = powerSystemsConfig.Value;
            _timeZoneService = timeZoneService;
            _modelContext = modelContext ?? throw new ArgumentNullException(nameof(modelContext));
            _weatherService = weatherService;

        }

        /// <summary>
        /// Gets the expected solar power from Now for the next 24 hours
        /// </summary>
        public double GetTotalSolarPowerExpected(List<HourlyInfo>? hourlyInfos)
        {
            if (hourlyInfos != null)
            { var currentTime = _timeZoneService.Now;

                var solarPower = 0.0;

                foreach (var hourlyInfo in hourlyInfos.Where(hi => hi.Time.Date >= currentTime.Date))
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
                var weatherData = _weatherService.WeatherData;

                if (weatherData != null && weatherData.UurVerwachting != null)
                {
                    StoreSolarRadiationData(weatherData);

                    foreach (UurVerwachting? uurVerwachting in weatherData.UurVerwachting)
                    {
                        if (uurVerwachting == null || uurVerwachting.Uur == null)
                            throw new InvalidOperationException($"Uurverwachting of Uur is null");

                        DateTime dateTime = DateTime.ParseExact(uurVerwachting.Uur, "dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

                        var currentHourlyInfo = hourlyInfos.Where(hi => hi.Time == dateTime).FirstOrDefault();

                        if (currentHourlyInfo != null)
                        {
                            foreach (var endpoint in _powerSystemsConfig.Endpoints.Values)
                            {
                                var longitude = endpoint.Longitude;
                                var latitude = endpoint.Latitude;

                                double solarAltitude;
                                double solarAzimuth;

                                CalculateSolarPosition(dateTime.Hour, latitude, longitude, endpoint.TimeZoneOffset, out solarAltitude, out solarAzimuth);

                                foreach (PhotoVoltaic solarPanel in endpoint.SolarPanels.Values)
                                {
                                    double solarFactor = GetSolarFactor(solarAzimuth, solarAltitude, solarPanel.Orientation, solarPanel.Tilt);

                                    // Historical deviation is 16.5 too high.
                                    solarFactor = solarFactor / 16.5; // TODO: Calculate 16.5 factor using historical data.

                                    currentHourlyInfo.SolarPower += CalculateSolarPower(uurVerwachting.GlobalRadiation, solarFactor, solarPanel, solarAltitude);

                                    currentHourlyInfo.SolarGlobalRadiation = uurVerwachting.GlobalRadiation;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void StoreSolarRadiationData(WeerData? weatherData)
        {
            foreach (var uurVerwachting in weatherData.UurVerwachting)
            {
                // if(_modelContext.SolarHistory.Count(sh => sh.Time == uurVerwachting.Timestamp))
            }
        }

        public double CalculateSolarPower(int globalRadiation, double solarFactor, PhotoVoltaic solarPanel, double solarAltitude)
        {
            double totalPeakPower = solarPanel.PanelCount * solarPanel.PeakPowerPerPanel;

            double altitudeFactor = (solarAltitude > 10) ? 1.0 : Math.Max(0, solarAltitude / 10.0);

            double powerkWatt = (globalRadiation / 1000.0) * totalPeakPower * solarPanel.Efficiency * solarFactor * altitudeFactor;

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
            Console.WriteLine($"Hour: {hour}, Solar Altitude: {altitude:F2}, Solar Azimuth: {azimuth:F2}, Hour Angle: {hourAngle:F2}");
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

            return Math.Max(0, Math.Cos(angleDifference * Math.PI / 180) * tiltFactor);
        }
    }
}
