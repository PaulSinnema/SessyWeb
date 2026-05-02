namespace SessyCommon.Configurations
{
    /// <summary>
    /// Configuration for the SolarEdge cloud API fallback.
    /// Add to appsettings.json under "SolarEdgeCloud":
    /// {
    ///   "SolarEdgeCloud": {
    ///     "SiteId": "your-site-id",
    ///     "ApiKey": "your-api-key"
    ///   }
    /// }
    /// Leave empty to disable the cloud fallback.
    /// </summary>
    public class SolarEdgeCloudConfig
    {
        /// <summary>
        /// SolarEdge site ID — found in the SolarEdge monitoring portal URL.
        /// </summary>
        public string SiteId { get; set; } = string.Empty;

        /// <summary>
        /// SolarEdge API key — generated via Admin → Site Access in the monitoring portal.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;
    }
}