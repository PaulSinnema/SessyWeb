using Microsoft.Extensions.Options;
using SessyCommon;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyData.Model;
using SessyData.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessyController.Services
{
    public class WeatherService : BackgroundService
    {
        private IHttpClientFactory _httpClientFactory { get; set; }
        private SolarDataService _solarDataService { get; set; }
        private LoggingService<SessyService> _logger { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private WeatherExpectancyConfig _WeatherExpectancyConfig { get; set; }

        private WeerData? WeatherData { get; set; }

        public bool Initialized { get; private set; }

        public WeatherService(LoggingService<SessyService> logger,
                              TimeZoneService timeZoneService,
                              IHttpClientFactory httpClientFactory,
                              SolarDataService solarDataService,
                              IOptions<WeatherExpectancyConfig> sunExpectancyConfig)
        {
            _logger = logger;
            _timeZoneService = timeZoneService;
            _httpClientFactory = httpClientFactory;
            _solarDataService = solarDataService;
            _WeatherExpectancyConfig = sunExpectancyConfig.Value;
        }

        private SemaphoreSlim WeatherDataSemaphore = new SemaphoreSlim(1);

        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("Weather service started ...");

            while (!cancelationToken.IsCancellationRequested)
            {
                Initialized = false;

                try
                {
                    await WeatherDataSemaphore.WaitAsync();

                    try
                    {
                        WeatherData = await GetWeatherDataAsync();

                        StoreWeatherData(WeatherData);

                    }
                    finally
                    {
                        WeatherDataSemaphore.Release();
                    }

                    Initialized = true;
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while getting the weather data.");
                }

                // Wait 30 minutes
                await Task.Delay(TimeSpan.FromMinutes(30), cancelationToken);
            }

            _logger.LogWarning("Weather service stopped.");
        }

        private void StoreWeatherData(WeerData? weatherData)
        {
            var statusList = new List<SolarData>();

            foreach (var uurverwachting in weatherData.UurVerwachting)
            {
                statusList.Add(new SolarData
                {
                    Time = uurverwachting.TimeStamp,
                    GlobalRadiation = uurverwachting.GlobalRadiation
                });
            }

            _solarDataService.AddOrUpdate(statusList, (item, set) => set.Where(sd => sd.Time == item.Time).FirstOrDefault());
        }

        private async Task<WeerData?> GetWeatherDataAsync()
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(_WeatherExpectancyConfig.BaseUrl);
            var response = await client.GetAsync($"?key={_WeatherExpectancyConfig.APIKey}&locatie={_WeatherExpectancyConfig.Location}");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                try
                {
                    var weerData = JsonSerializer.Deserialize<WeerData>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    return weerData;
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, $"Error trying to deserialize reponse from WeerOnline:\n\nResponse:{response}\n\nContent:{content}");
                    throw;
                }
            }
            else
            {
                // Foutafhandeling
                throw new Exception($"Error getting weather data from WeerOnline: {response.ReasonPhrase}");
            }
        }

        public WeerData? GetWeatherData()
        {
            WeatherDataSemaphore.Wait();

            try
            {
                return WeatherData;
            }
            finally
            {
                WeatherDataSemaphore.Release();
            }
        }

        public double GetCurrentTemperature()
        {
            var now = _timeZoneService.Now.Date.AddHours(_timeZoneService.Now.Hour);

            return WeatherData.UurVerwachting
                .Where(uv => uv.TimeStamp.HasValue &&
                             uv.TimeStamp.Value.Date.AddHours(uv.TimeStamp.Value.Hour) == now)
                .Select(uv => uv.Temp)
                .First();
        }

        public int? GetTemperature(DateTime time)
        {
            UurVerwachting? data = null;
            time = time.DateHour();

            if (WeatherData != null)
            {
                data = WeatherData.UurVerwachting.Where(uv =>
                {
                    var date = DateTime.ParseExact(uv.Uur!, "dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture).DateHour();
                    return date == time;
                }).FirstOrDefault();
            }

            return data == null ? null : data.Temp;
        }

        public (double? temperature, double? globalRadiation)? GetCurrentWeather()
        {
            if (WeatherData != null)
            {
                var now = _timeZoneService.Now.Date.AddHours(_timeZoneService.Now.Hour);
                var liveWeer = WeatherData?.LiveWeer?.FirstOrDefault();


                var currentWeather = WeatherData.UurVerwachting
                    .Where(uv => uv.TimeStamp.HasValue &&
                                 uv.TimeStamp.Value.Hour == now.Hour)
                    .FirstOrDefault();

                return (currentWeather?.Temp, currentWeather?.GlobalRadiation);
            }

            return null;
        }

        public class LiveWeer
        {
            [JsonPropertyName("plaats")]
            public string Plaats { get; set; } = string.Empty;

            [JsonPropertyName("timestamp")]
            public long Timestamp { get; set; }

            [JsonPropertyName("time")]
            public string Time { get; set; } = string.Empty;

            [JsonPropertyName("temp")]
            public double Temp { get; set; }

            [JsonPropertyName("gtemp")]
            public double Gevoelstemperatuur { get; set; }

            [JsonPropertyName("samenv")]
            public string Samenvatting { get; set; } = string.Empty;

            [JsonPropertyName("lv")]
            public int Luchtvochtigheid { get; set; }

            [JsonPropertyName("windr")]
            public string Windrichting { get; set; } = string.Empty;

            [JsonPropertyName("windrgr")]
            public double WindrichtingGraden { get; set; }

            [JsonPropertyName("windms")]
            public double WindSnelheidMs { get; set; }

            [JsonPropertyName("windbft")]
            public double WindkrachtBft { get; set; }

            [JsonPropertyName("windknp")]
            public double WindSnelheidKnopen { get; set; }

            [JsonPropertyName("windkmh")]
            public double WindSnelheidKmh { get; set; }

            [JsonPropertyName("luchtd")]
            public double Luchtdruk { get; set; }

            [JsonPropertyName("ldmmhg")]
            public double LuchtdrukMmhg { get; set; }

            [JsonPropertyName("dauwp")]
            public double Dauwpunt { get; set; }

            [JsonPropertyName("zicht")]
            public double ZichtMeters { get; set; }

            [JsonPropertyName("gr")]
            public double GlobalRadiation { get; set; }

            [JsonPropertyName("verw")]
            public string Verwachting { get; set; } = string.Empty;

            [JsonPropertyName("sup")]
            public string ZonOpkomst { get; set; } = string.Empty;

            [JsonPropertyName("sunder")]
            public string ZonOndergang { get; set; } = string.Empty;

            [JsonPropertyName("image")]
            public string Weerbeeld { get; set; } = string.Empty;

            [JsonPropertyName("alarm")]
            public int Alarm { get; set; }

            [JsonPropertyName("lkop")]
            public string LaatsteKop { get; set; } = string.Empty;

            [JsonPropertyName("ltekst")]
            public string LaatsteTekst { get; set; } = string.Empty;

            [JsonPropertyName("wrschklr")]
            public string WaarschuwingKleur { get; set; } = string.Empty;

            [JsonPropertyName("wrsch_g")]
            public string WaarschuwingGevaar { get; set; } = string.Empty;

            [JsonPropertyName("wrsch_gts")]
            public int WaarschuwingGevaarTotS { get; set; }

            [JsonPropertyName("wrsch_gc")]
            public string WaarschuwingGevaarCode { get; set; } = string.Empty;
        }

        public class DagVerwachting
        {
            [JsonPropertyName("dag")]
            public string Dag { get; set; } = string.Empty;

            [JsonPropertyName("image")]
            public string Weerbeeld { get; set; } = string.Empty;

            [JsonPropertyName("max_temp")]
            public int MaxTemp { get; set; }

            [JsonPropertyName("min_temp")]
            public int MinTemp { get; set; }

            [JsonPropertyName("windbft")]
            public int WindkrachtBft { get; set; }

            [JsonPropertyName("windkmh")]
            public int WindSnelheidKmh { get; set; }

            [JsonPropertyName("windknp")]
            public int WindSnelheidKnopen { get; set; }

            [JsonPropertyName("windms")]
            public int WindSnelheidMs { get; set; }

            [JsonPropertyName("windrgr")]
            public int WindrichtingGraden { get; set; }

            [JsonPropertyName("windr")]
            public string Windrichting { get; set; } = string.Empty;

            [JsonPropertyName("neersl_perc_dag")]
            public int NeerslagKans { get; set; }

            [JsonPropertyName("zond_perc_dag")]
            public int ZonDuurPercDag { get; set; }
        }

        public class UurVerwachting
        {
            [JsonPropertyName("uur")]
            public string? Uur { get; set; }

            [JsonPropertyName("timestamp")]
            public long? UnixTimestamp { get; set; }

            /// <summary>
            /// DateTime stamp converted from Unix timestamp long.
            /// </summary>
            [JsonIgnore]
            public DateTime? TimeStamp => TimeZoneService.FromUnixTime(UnixTimestamp);

            [JsonPropertyName("image")]
            public string Weerbeeld { get; set; } = string.Empty;

            [JsonPropertyName("temp")]
            public int Temp { get; set; }

            [JsonPropertyName("windbft")]
            public int WindkrachtBft { get; set; }

            [JsonPropertyName("windkmh")]
            public int WindSnelheidKmh { get; set; }

            [JsonPropertyName("windknp")]
            public int WindSnelheidKnopen { get; set; }

            [JsonPropertyName("windms")]
            public int WindSnelheidMs { get; set; }

            [JsonPropertyName("windrgr")]
            public int WindrichtingGraden { get; set; }

            [JsonPropertyName("windr")]
            public string Windrichting { get; set; } = string.Empty;

            [JsonPropertyName("neersl")]
            public double Neerslag { get; set; }

            [JsonPropertyName("gr")]
            public int GlobalRadiation { get; set; }

            public override string ToString()
            {
                return $"{Uur}: Temp: {Temp}";
            }
        }

        public class ApiInfo
        {
            [JsonPropertyName("bron")]
            public string Bron { get; set; } = string.Empty;

            [JsonPropertyName("max_verz")]
            public int MaxVerzoeken { get; set; }

            [JsonPropertyName("rest_verz")]
            public int ResterendeVerzoeken { get; set; }
        }

        public class WeerData
        {
            [JsonPropertyName("liveweer")]
            public List<LiveWeer>? LiveWeer { get; set; }

            [JsonPropertyName("wk_verw")]
            public List<DagVerwachting>? WeekVerwachting { get; set; }

            [JsonPropertyName("uur_verw")]
            public List<UurVerwachting> UurVerwachting { get; set; } = new List<UurVerwachting>();

            [JsonPropertyName("api")]
            public List<ApiInfo>? Api { get; set; }
        }
    }
}
