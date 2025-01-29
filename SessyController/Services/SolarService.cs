using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;
using System.Globalization;
using static SessyController.Services.WeatherExpectancyService;

namespace SessyController.Services
{
    public class SolarService
    {
        private IConfiguration _configuration { get; set; }
        private LoggingService<SolarEdgeService> _logger { get; set; }

        private WeatherExpectancyService _weatherExpectancyService { get; set; }

        private PowerSystemsConfig _powerSystemsConfig;

        public SolarService(IConfiguration configuration,
                                      LoggingService<SolarEdgeService> logger,
                                      WeatherExpectancyService weatherExpectancyService,
                                      IOptions<PowerSystemsConfig> powerSystemsConfig)
        {
            _configuration = configuration;
            _logger = logger;
            _weatherExpectancyService = weatherExpectancyService;
            _powerSystemsConfig = powerSystemsConfig.Value;
        }

        public class DateTimeFormatProvider : IFormatProvider
        {
            public object? GetFormat(Type? formatType)
            {
                throw new NotImplementedException();
            }
        }

        public async Task GetSolarPower(List<HourlyInfo> hourlyInfos)
        {
            var weatherData = await _weatherExpectancyService.GetWeerDataAsync();

            if (weatherData != null && weatherData.UurVerwachting != null)
            {
                foreach (var uurVerwachting in weatherData.UurVerwachting)
                {
                    DateTime dateTime = DateTime.ParseExact(uurVerwachting.Uur, "dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

                    var currentHourlyInfo = hourlyInfos.Where(hi => hi.Time == dateTime).FirstOrDefault();

                    if (currentHourlyInfo != null)
                    {
                        foreach (var endpoint in _powerSystemsConfig.Endpoints)
                        {
                            var longitude = endpoint.Value.Longitude;
                            var latitude = endpoint.Value.Latitude;

                            foreach (var solarPanel in endpoint.Value.SolarPanels)
                            {
                                double solarAltitude;
                                double solarAzimuth;

                                CalculateSolarPosition(dateTime.Hour, latitude, longitude, endpoint.Value.TimeZoneOffset, out solarAltitude, out solarAzimuth);

                                double solarFactor = GetSolarFactor(solarAzimuth, solarPanel.Value.Orientation);
                                currentHourlyInfo.SolarPower += CalculateSolarPower(uurVerwachting.GlobalRadiation, solarFactor, solarPanel.Value);

                                currentHourlyInfo.SolarGlobalRadiation = uurVerwachting.GlobalRadiation;
                            }
                        }
                    }
                }
            }
        }

        public double CalculateSolarPower(int globalRadiation, double solarFactor, PhotoVoltaic solarPanel)
        {
            var test = solarPanel.TotalArea;
            double totalPeakPower = solarPanel.PanelCount * solarPanel.PeakPowerPerPanel;
            return (globalRadiation / 1000.0) * totalPeakPower * solarPanel.Efficiency * solarFactor;
        }

        private void CalculateSolarPosition(int hour, double latitude, double longitude, int timezoneOffset, out double altitude, out double azimuth)
        {
            // Approximate solar position calculation based on time, latitude, and longitude
            double declination = 23.45 * Math.Sin((2 * Math.PI / 365) * (172 - 81)); // Solar declination angle
            double hourAngle = 15 * (hour - 12 + timezoneOffset); // Hour angle
            double latRad = latitude * Math.PI / 180.0;
            double decRad = declination * Math.PI / 180.0;
            double haRad = hourAngle * Math.PI / 180.0;

            altitude = Math.Asin(Math.Sin(latRad) * Math.Sin(decRad) + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad)) * 180.0 / Math.PI;

            double sinAzimuth = -Math.Sin(haRad) * Math.Cos(decRad) / Math.Cos(altitude * Math.PI / 180);
            azimuth = Math.Asin(sinAzimuth) * 180.0 / Math.PI;

            if (hour > 12) azimuth = 180 - azimuth;
            if (azimuth < 0) azimuth += 360;
        }

        private double GetSolarFactor(double solarAzimuth, string? orientation)
        {
            // Dictionary for panel orientations mapped to azimuth angles
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

            if (!orientations.ContainsKey(orientation.ToLower()))
                throw new InvalidOperationException($"Unknown orientation: {orientation}");

            double panelAzimuth = orientations[orientation.ToLower()];
            double angleDifference = Math.Abs(panelAzimuth - solarAzimuth);

            if (angleDifference > 180)
                angleDifference = 360 - angleDifference;

            var factor = Math.Max(0, Math.Cos(angleDifference * Math.PI / 180));

            return factor;
        }
    }
}
