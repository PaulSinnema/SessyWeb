using Microsoft.Extensions.DependencyInjection;
using SessyCommon.Services;

namespace SessyCommon.Configurations
{
    public class SettingsConfig
    {
        /// <summary>
        /// Where your house is situated.
        /// </summary>
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        /// <summary>
        /// If set to true will put Charged (Sessy) in control
        /// </summary>
        public bool ChargedInControl { get; set; }
        /// <summary>
        /// If set to true will use manual hours rather than automated hours.
        /// </summary>
        public bool ManualOverride { get; set; }
        /// <summary>
        /// List of hours to charge when manual is set to true.
        /// </summary>
        public List<int>? ManualChargingHours { get; set; }

        /// <summary>
        /// List of hours to discharge when manual is set to true.
        /// </summary>
        public List<int>? ManualDischargingHours { get; set; }

        /// <summary>
        /// List of hours to net zero home when manual is set to true.
        /// </summary>
        public List<int>? ManualNetZeroHomeHours { get; set; }

        /// <summary>
        /// The timezone the container is running for.
        /// </summary>
        public string? Timezone { get; set; }
        /// <summary>
        /// Estimated cost for one whole cycle of (dis)charging.
        /// </summary>
        public double CycleCost {  get; set; }

        /// <summary>
        /// Minimum profit for Net Zero Home to be enabled in non (dis)charging hours
        /// </summary>
        public double NetZeroHomeMinProfit { get; set; }

        /// <summary>
        /// How much is needed for the home (this will have to be estimated later).
        /// </summary>
        public List<double>? RequiredHomeEnergy { get; set; }

        public double EnergyNeedsPerMonth
        {
            get
            {
                var timeZoneService = ServiceLocator.ServiceProvider!.GetRequiredService<TimeZoneService>();
                var month = timeZoneService.Now.Month - 1;
                return RequiredHomeEnergy![month];
            }
        }

        public bool SolarSystemShutsDownDuringNegativePrices { get; set; }

        public double SolarCorrection { get; set; }

        public string? DatabaseBackupDirectory { get; set; }
    }
}
