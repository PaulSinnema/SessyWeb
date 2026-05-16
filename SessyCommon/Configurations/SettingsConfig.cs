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
        public double CycleCost { get; set; }

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

        /// <summary>
        /// Known average annual solar production in kWh.
        /// Used to scale measured seasonal savings when less than 6 months
        /// of measurement data is available. Has no effect once sufficient
        /// data exists. Example: 3250 for a system producing 3250 kWh/year.
        /// </summary>
        public double SolarAnnualProductionKWh { get; set; } = 0.0;

        public string? DatabaseBackupDirectory { get; set; }

        /// <summary>
        /// Optional earliest date from which statistics are calculated.
        /// Use this to exclude unreliable historical data (e.g. caused by hardware
        /// issues or firmware bugs) without deleting records from the database.
        /// When not set, statistics are calculated over all available data.
        /// Format: "yyyy-MM-dd" in appsettings.json, e.g. "2026-03-01".
        /// </summary>
        public DateTime? StatisticsFromDate { get; set; }
    }
}