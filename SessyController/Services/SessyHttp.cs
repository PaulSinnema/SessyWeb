using Newtonsoft.Json;
using SessyCommon.Configurations;
using System.Net.Http.Headers;
using System.Text;

namespace SessyController.Services
{
    /// <summary>HTTP to the local Sessy APIs (batteries and P1 meters): basic auth, JSON in and out.</summary>
    internal static class SessyHttp
    {
        /// <summary>Client for one device. Throws when the address, or with requireCredentials the credentials, are missing.</summary>
        public static HttpClient CreateClient(IHttpClientFactory factory, SessyDeviceEndpoint device, string id,
                                              TimeSpan? timeout = null, bool requireCredentials = false)
        {
            if (string.IsNullOrWhiteSpace(device.BaseUrl))
                throw new InvalidOperationException($"BaseUrl for Sessy device '{id}' is empty");

            if (requireCredentials && (string.IsNullOrWhiteSpace(device.UserId) || string.IsNullOrWhiteSpace(device.Password)))
                throw new InvalidOperationException($"UserId or Password for Sessy device '{id}' is empty");

            var client = factory.CreateClient();
            client.BaseAddress = new Uri(device.BaseUrl);

            if (timeout.HasValue)
                client.Timeout = timeout.Value;

            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{device.UserId}:{device.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            return client;
        }

        /// <summary>GET and deserialize; throws on a non-success status.</summary>
        public static async Task<T?> GetJsonAsync<T>(this HttpClient client, string path)
        {
            var response = await client.GetAsync(path).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonConvert.DeserializeObject<T>(content);
        }

        /// <summary>POST the body as JSON; throws on a non-success status.</summary>
        public static async Task PostJsonAsync(this HttpClient client, string path, object body)
        {
            var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(path, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }
}
