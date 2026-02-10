using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessyController.Services
{
    // ---------------------------
    // Configuration
    // ---------------------------
    public sealed class KnmiApiOptions
    {
        // API token for KNMI Data Platform (Open Data API and/or EDR API).
        public string ApiToken { get; init; } = string.Empty;

        // Base URLs as documented by KNMI.
        public Uri OpenDataApiBaseUrl { get; init; } = new("https://api.dataplatform.knmi.nl/open-data/v1/");
        public Uri EnvironmentalDataRetrievalApiBaseUrl { get; init; } = new("https://api.dataplatform.knmi.nl/edr/v1/");

        // EDR collection name for near-real-time observations.
        public string TenMinuteObservationsCollectionName { get; init; } = "10-minute-in-situ-meteorological-observations";

        // Open Data datasets (file-based).
        public string WeatherWarningsDatasetName { get; init; } = "waarschuwingen_nederland_48h";
        public string WeatherWarningsDatasetVersion { get; init; } = "1.0";

        public string ShortTermTextForecastDatasetName { get; init; } = "short-term-weather-forecast";
        public string ShortTermTextForecastDatasetVersion { get; init; } = "1.0";

        public string HarmonieHourlyForecastDatasetName { get; init; } = "harmonie_arome_cy43_p1";
        public string HarmonieHourlyForecastDatasetVersion { get; init; } = "1.0";
    }

    // ---------------------------
    // Open Data API response models
    // ---------------------------
    public sealed class KnmiOpenDataListFilesResponse
    {
        [JsonPropertyName("files")]
        public List<KnmiOpenDataFileItem> Files { get; set; } = new();

        [JsonPropertyName("nextPageToken")]
        public string? NextPageToken { get; set; }
    }

    public sealed class KnmiOpenDataFileItem
    {
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long SizeInBytes { get; set; }

        [JsonPropertyName("created")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("lastModified")]
        public DateTimeOffset? LastModifiedAt { get; set; }
    }

    public sealed class KnmiOpenDataTemporaryDownloadUrlResponse
    {
        [JsonPropertyName("temporaryDownloadUrl")]
        public string TemporaryDownloadUrl { get; set; } = string.Empty;
    }

    // ---------------------------
    // EDR (GeoJSON) response models (minimal / flexible)
    // ---------------------------
    public sealed class KnmiEdrLocationsResponse
    {
        [JsonPropertyName("features")]
        public List<KnmiEdrLocationFeature> Features { get; set; } = new();
    }

    public sealed class KnmiEdrLocationFeature
    {
        [JsonPropertyName("id")]
        public string LocationId { get; set; } = string.Empty;

        [JsonPropertyName("geometry")]
        public KnmiEdrGeometry? Geometry { get; set; }

        // Properties differ per collection; keep it flexible.
        [JsonPropertyName("properties")]
        public JsonElement Properties { get; set; }
    }

    public sealed class KnmiEdrGeometry
    {
        [JsonPropertyName("type")]
        public string GeometryType { get; set; } = string.Empty;

        // GeoJSON for Point: [longitude, latitude]
        [JsonPropertyName("coordinates")]
        public double[] CoordinatesLongitudeLatitude { get; set; } = Array.Empty<double>();
    }

    // ---------------------------
    // Open Data API client
    // ---------------------------
    public interface IKnmiOpenDataApiClient
    {
        Task<KnmiOpenDataListFilesResponse> ListFilesAsync(
            string datasetName,
            string datasetVersion,
            int maximumNumberOfFiles = 10,
            string sortingDirection = "desc",
            string orderByField = "created",
            string? nextPageToken = null,
            string? beginTimestampFilter = null,
            string? endTimestampFilter = null,
            CancellationToken cancellationToken = default);

        Task<Uri> GetTemporaryDownloadUrlAsync(
            string datasetName,
            string datasetVersion,
            string filename,
            CancellationToken cancellationToken = default);

        Task<byte[]> DownloadFileAsync(Uri temporaryDownloadUrl, CancellationToken cancellationToken = default);
    }

    public sealed class KnmiOpenDataApiClient : IKnmiOpenDataApiClient
    {
        private readonly HttpClient httpClient;
        private readonly KnmiApiOptions options;

        private static readonly JsonSerializerOptions jsonSerializerOptions = new(JsonSerializerDefaults.Web);

        public KnmiOpenDataApiClient(HttpClient httpClient, KnmiApiOptions options)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.options = options ?? throw new ArgumentNullException(nameof(options));

            this.httpClient.BaseAddress = options.OpenDataApiBaseUrl;

            // KNMI docs often show: Authorization: <API_KEY> (no Bearer).
            // If your token must be used as a raw Authorization header value, keep this.
            this.httpClient.DefaultRequestHeaders.Remove("Authorization");
            this.httpClient.DefaultRequestHeaders.Add("Authorization", options.ApiToken);
        }

        public async Task<KnmiOpenDataListFilesResponse> ListFilesAsync(
            string datasetName,
            string datasetVersion,
            int maximumNumberOfFiles = 10,
            string sortingDirection = "desc",
            string orderByField = "created",
            string? nextPageToken = null,
            string? beginTimestampFilter = null,
            string? endTimestampFilter = null,
            CancellationToken cancellationToken = default)
        {
            var relativePath = $"datasets/{datasetName}/versions/{datasetVersion}/files";

            var queryParameters = new List<string>
        {
            $"maxKeys={maximumNumberOfFiles}",
            $"sorting={Uri.EscapeDataString(sortingDirection)}",
            $"orderBy={Uri.EscapeDataString(orderByField)}",
        };

            if (!string.IsNullOrWhiteSpace(nextPageToken))
                queryParameters.Add($"nextPageToken={Uri.EscapeDataString(nextPageToken)}");

            if (!string.IsNullOrWhiteSpace(beginTimestampFilter))
                queryParameters.Add($"begin={Uri.EscapeDataString(beginTimestampFilter)}");

            if (!string.IsNullOrWhiteSpace(endTimestampFilter))
                queryParameters.Add($"end={Uri.EscapeDataString(endTimestampFilter)}");

            var requestUri = $"{relativePath}?{string.Join("&", queryParameters)}";

            using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize<KnmiOpenDataListFilesResponse>(responseBody, jsonSerializerOptions)
                   ?? new KnmiOpenDataListFilesResponse();
        }

        public async Task<Uri> GetTemporaryDownloadUrlAsync(
            string datasetName,
            string datasetVersion,
            string filename,
            CancellationToken cancellationToken = default)
        {
            var relativePath =
                $"datasets/{datasetName}/versions/{datasetVersion}/files/{Uri.EscapeDataString(filename)}/url";

            using var response = await httpClient.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var temporaryUrlResponse =
                JsonSerializer.Deserialize<KnmiOpenDataTemporaryDownloadUrlResponse>(responseBody, jsonSerializerOptions)
                ?? throw new InvalidOperationException("Could not deserialize the temporary download URL response.");

            return new Uri(temporaryUrlResponse.TemporaryDownloadUrl);
        }

        public async Task<byte[]> DownloadFileAsync(Uri temporaryDownloadUrl, CancellationToken cancellationToken = default)
        {
            using var response = await httpClient.GetAsync(temporaryDownloadUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------
    // EDR API client
    // ---------------------------
    public interface IKnmiEnvironmentalDataRetrievalClient
    {
        Task<KnmiEdrLocationsResponse> GetLocationsAsync(string collectionName, CancellationToken cancellationToken = default);

        Task<JsonDocument> QueryLocationAsync(
            string collectionName,
            string locationId,
            string parameterNamesCsv,
            string datetimeIsoOrIsoRange,
            CancellationToken cancellationToken = default);
    }

    public sealed class KnmiEnvironmentalDataRetrievalClient : IKnmiEnvironmentalDataRetrievalClient
    {
        private readonly HttpClient httpClient;
        private readonly KnmiApiOptions options;

        public KnmiEnvironmentalDataRetrievalClient(HttpClient httpClient, KnmiApiOptions options)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.options = options ?? throw new ArgumentNullException(nameof(options));

            this.httpClient.BaseAddress = options.EnvironmentalDataRetrievalApiBaseUrl;

            // KNMI docs often show: Authorization: <API_KEY> (no Bearer).
            this.httpClient.DefaultRequestHeaders.Remove("Authorization");
            this.httpClient.DefaultRequestHeaders.Add("Authorization", options.ApiToken);
        }

        public async Task<KnmiEdrLocationsResponse> GetLocationsAsync(string collectionName, CancellationToken cancellationToken = default)
        {
            var relativePath = $"collections/{collectionName}/locations";

            using var response = await httpClient.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize<KnmiEdrLocationsResponse>(responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? new KnmiEdrLocationsResponse();
        }

        public async Task<JsonDocument> QueryLocationAsync(
            string collectionName,
            string locationId,
            string parameterNamesCsv,
            string datetimeIsoOrIsoRange,
            CancellationToken cancellationToken = default)
        {
            var relativePath =
                $"collections/{collectionName}/locations/{Uri.EscapeDataString(locationId)}" +
                $"?datetime={Uri.EscapeDataString(datetimeIsoOrIsoRange)}" +
                $"&parameter-name={Uri.EscapeDataString(parameterNamesCsv)}";

            using var response = await httpClient.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------
    // Multi-source gateway
    // ---------------------------
    public sealed class KnmiWeatherDataGateway
    {
        private readonly IKnmiEnvironmentalDataRetrievalClient environmentalDataRetrievalClient;
        private readonly IKnmiOpenDataApiClient openDataApiClient;
        private readonly KnmiApiOptions options;

        private KnmiEdrLocationsResponse? cachedStationLocations;
        private DateTimeOffset cachedStationLocationsAtUtc;

        public KnmiWeatherDataGateway(
            IKnmiEnvironmentalDataRetrievalClient environmentalDataRetrievalClient,
            IKnmiOpenDataApiClient openDataApiClient,
            KnmiApiOptions options)
        {
            this.environmentalDataRetrievalClient = environmentalDataRetrievalClient ?? throw new ArgumentNullException(nameof(environmentalDataRetrievalClient));
            this.openDataApiClient = openDataApiClient ?? throw new ArgumentNullException(nameof(openDataApiClient));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Gets current observations from the nearest station to the provided latitude/longitude.
        /// Returns raw JSON; you can map it to your domain model in a separate layer.
        /// </summary>
        public async Task<JsonDocument> GetCurrentObservationsForNearestStationAsync(
            double latitude,
            double longitude,
            string parameterNamesCsv = "ta,rh,dd,ff,pp,td,vv,qg,ww",
            TimeSpan? lookbackWindow = null,
            CancellationToken cancellationToken = default)
        {
            lookbackWindow ??= TimeSpan.FromHours(1);

            var stations = await GetCachedStationLocationsAsync(cancellationToken).ConfigureAwait(false);

            var nearestStation = FindNearestStationByHaversineDistance(stations, latitude, longitude);

            var endTimeUtc = DateTimeOffset.UtcNow;
            var startTimeUtc = endTimeUtc - lookbackWindow.Value;

            var datetimeRange =
                $"{startTimeUtc:yyyy-MM-ddTHH:mm:ssZ}/{endTimeUtc:yyyy-MM-ddTHH:mm:ssZ}";

            return await environmentalDataRetrievalClient.QueryLocationAsync(
                options.TenMinuteObservationsCollectionName,
                nearestStation.LocationId,
                parameterNamesCsv,
                datetimeRange,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads the newest KNMI warning file.
        /// </summary>
        public async Task<(string Filename, byte[] Content)> DownloadLatestWarningsFileAsync(CancellationToken cancellationToken = default)
        {
            var latestFile = await GetLatestFileAsync(options.WeatherWarningsDatasetName, options.WeatherWarningsDatasetVersion, cancellationToken).ConfigureAwait(false);
            var temporaryDownloadUrl = await openDataApiClient.GetTemporaryDownloadUrlAsync(options.WeatherWarningsDatasetName, options.WeatherWarningsDatasetVersion, latestFile.Filename, cancellationToken).ConfigureAwait(false);
            var content = await openDataApiClient.DownloadFileAsync(temporaryDownloadUrl, cancellationToken).ConfigureAwait(false);
            return (latestFile.Filename, content);
        }

        /// <summary>
        /// Downloads the newest KNMI short-term text forecast file.
        /// </summary>
        public async Task<(string Filename, byte[] Content)> DownloadLatestShortTermTextForecastFileAsync(CancellationToken cancellationToken = default)
        {
            var latestFile = await GetLatestFileAsync(options.ShortTermTextForecastDatasetName, options.ShortTermTextForecastDatasetVersion, cancellationToken).ConfigureAwait(false);
            var temporaryDownloadUrl = await openDataApiClient.GetTemporaryDownloadUrlAsync(options.ShortTermTextForecastDatasetName, options.ShortTermTextForecastDatasetVersion, latestFile.Filename, cancellationToken).ConfigureAwait(false);
            var content = await openDataApiClient.DownloadFileAsync(temporaryDownloadUrl, cancellationToken).ConfigureAwait(false);
            return (latestFile.Filename, content);
        }

        /// <summary>
        /// Downloads the newest HARMONIE GRIB file (can be large).
        /// </summary>
        public async Task<(string Filename, byte[] Content)> DownloadLatestHarmonieGribFileAsync(CancellationToken cancellationToken = default)
        {
            var latestFile = await GetLatestFileAsync(options.HarmonieHourlyForecastDatasetName, options.HarmonieHourlyForecastDatasetVersion, cancellationToken).ConfigureAwait(false);
            var temporaryDownloadUrl = await openDataApiClient.GetTemporaryDownloadUrlAsync(options.HarmonieHourlyForecastDatasetName, options.HarmonieHourlyForecastDatasetVersion, latestFile.Filename, cancellationToken).ConfigureAwait(false);
            var content = await openDataApiClient.DownloadFileAsync(temporaryDownloadUrl, cancellationToken).ConfigureAwait(false);
            return (latestFile.Filename, content);
        }

        // ---------------------------
        // Internal helpers
        // ---------------------------
        private async Task<KnmiEdrLocationsResponse> GetCachedStationLocationsAsync(CancellationToken cancellationToken)
        {
            // Cache station list for 6 hours; adjust to your preference.
            if (cachedStationLocations != null &&
                DateTimeOffset.UtcNow - cachedStationLocationsAtUtc < TimeSpan.FromHours(6))
            {
                return cachedStationLocations;
            }

            cachedStationLocations = await environmentalDataRetrievalClient
                .GetLocationsAsync(options.TenMinuteObservationsCollectionName, cancellationToken)
                .ConfigureAwait(false);

            cachedStationLocationsAtUtc = DateTimeOffset.UtcNow;

            return cachedStationLocations;
        }

        private static KnmiEdrLocationFeature FindNearestStationByHaversineDistance(
            KnmiEdrLocationsResponse stationLocations,
            double targetLatitude,
            double targetLongitude)
        {
            if (stationLocations.Features.Count == 0)
                throw new InvalidOperationException("No station locations were returned by KNMI EDR API.");

            KnmiEdrLocationFeature? nearestStation = null;
            double nearestDistanceKm = double.MaxValue;

            foreach (var feature in stationLocations.Features)
            {
                var coordinates = feature.Geometry?.CoordinatesLongitudeLatitude;
                if (coordinates == null || coordinates.Length < 2)
                    continue;

                var stationLongitude = coordinates[0];
                var stationLatitude = coordinates[1];

                var distanceKm = CalculateHaversineDistanceKm(
                    targetLatitude,
                    targetLongitude,
                    stationLatitude,
                    stationLongitude);

                if (distanceKm < nearestDistanceKm)
                {
                    nearestDistanceKm = distanceKm;
                    nearestStation = feature;
                }
            }

            return nearestStation ?? throw new InvalidOperationException("Could not determine a nearest station (missing geometry).");
        }

        private static double CalculateHaversineDistanceKm(
            double latitude1,
            double longitude1,
            double latitude2,
            double longitude2)
        {
            const double earthRadiusKm = 6371.0;

            static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

            var deltaLatitude = ToRadians(latitude2 - latitude1);
            var deltaLongitude = ToRadians(longitude2 - longitude1);

            var a =
                Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2) +
                Math.Cos(ToRadians(latitude1)) * Math.Cos(ToRadians(latitude2)) *
                Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2);

            var c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
            return earthRadiusKm * c;
        }

        private async Task<KnmiOpenDataFileItem> GetLatestFileAsync(string datasetName, string datasetVersion, CancellationToken cancellationToken)
        {
            var response = await openDataApiClient.ListFilesAsync(
                datasetName: datasetName,
                datasetVersion: datasetVersion,
                maximumNumberOfFiles: 1,
                sortingDirection: "desc",
                orderByField: "created",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (response.Files.Count == 0)
                throw new InvalidOperationException($"Dataset '{datasetName}' version '{datasetVersion}' returned no files.");

            return response.Files[0];
        }
    }
}
