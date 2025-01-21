using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SessyController.Configurations;

namespace SessyController.Services
{
    public class SunExpectancyService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly LoggingService<SessyService> _logger;
        private readonly SunExpectancyConfig _sunExpectancyConfig;

        public SunExpectancyService(LoggingService<SessyService> logger,
                        IHttpClientFactory httpClientFactory,
                        IOptions<SunExpectancyConfig> sunExpectancyConfig)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _sunExpectancyConfig = sunExpectancyConfig.Value;
        }

        public async Task<WeerData?> GetWeerDataAsync()
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(_sunExpectancyConfig.BaseUrl);
            var response = await client.GetAsync($"?key={_sunExpectancyConfig.APIKey}&locatie={_sunExpectancyConfig.Location}");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var weerData = JsonSerializer.Deserialize<WeerData>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return weerData;
            }
            else
            {
                // Foutafhandeling
                throw new Exception($"Fout bij het ophalen van weergegevens: {response.ReasonPhrase}");
            }
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
            public int WindkrachtBft { get; set; }

            [JsonPropertyName("windknp")]
            public double WindSnelheidKnopen { get; set; }

            [JsonPropertyName("windkmh")]
            public double WindSnelheidKmh { get; set; }

            [JsonPropertyName("luchtd")]
            public double Luchtdruk { get; set; }

            [JsonPropertyName("ldmmhg")]
            public int LuchtdrukMmhg { get; set; }

            [JsonPropertyName("dauwp")]
            public double Dauwpunt { get; set; }

            [JsonPropertyName("zicht")]
            public int ZichtMeters { get; set; }

            [JsonPropertyName("gr")]
            public int GlobalRadiation { get; set; }

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
            public string Uur { get; set; } = string.Empty;

            [JsonPropertyName("timestamp")]
            public long Timestamp { get; set; }

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
            public List<UurVerwachting>? UurVerwachting { get; set; }

            [JsonPropertyName("api")]
            public List<ApiInfo>? Api { get; set; }
        }
    }
}
