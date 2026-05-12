namespace SessyCommon.Configurations
{
    /// <summary>
    /// Configuration for heat pump savings calculation.
    /// Used to estimate annual savings compared to the old gas heating situation.
    ///
    /// Add to appsettings.json:
    /// "HeatPumpConfig": {
    ///   "AnnualGasConsumptionM3": 950,
    ///   "GasPriceEurPerM3": 1.45,
    ///   "GasStandingChargeEurPerYear": 185.0,
    ///   "InstallationDate": "2024-03-01"
    /// }
    /// </summary>
    public class HeatPumpConfig
    {
        /// <summary>
        /// Annual gas consumption in m³ before heat pump installation.
        /// </summary>
        public double AnnualGasConsumptionM3 { get; set; } = 0.0;

        /// <summary>
        /// Gas price in EUR per m³ (including taxes).
        /// </summary>
        public double GasPriceEurPerM3 { get; set; } = 0.0;

        /// <summary>
        /// Annual gas standing charge in EUR (vastrecht).
        /// No longer paid after heat pump installation.
        /// </summary>
        public double GasStandingChargeEurPerYear { get; set; } = 0.0;

        /// <summary>
        /// Date the heat pump was installed.
        /// Used to calculate how many months of savings have accrued.
        /// </summary>
        public DateTime InstallationDate { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Calculated annual gas cost saved (excluding standing charge).
        /// </summary>
        public double AnnualGasCostSavedEur => AnnualGasConsumptionM3 * GasPriceEurPerM3;

        /// <summary>
        /// Total annual savings including standing charge.
        /// </summary>
        public double TotalAnnualSavingsEur => AnnualGasCostSavedEur + GasStandingChargeEurPerYear;

        /// <summary>
        /// Monthly savings.
        /// </summary>
        public double MonthlyAverageSavingsEur => TotalAnnualSavingsEur / 12.0;

        /// <summary>
        /// True when heat pump config is properly configured.
        /// </summary>
        public bool IsConfigured => AnnualGasConsumptionM3 > 0 &&
                                    GasPriceEurPerM3 > 0 &&
                                    InstallationDate > DateTime.MinValue;
    }
}