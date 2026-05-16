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
    ///   "AnnualElectricityConsumptionKWh": 2000,
    ///   "ElectricityPriceEurPerKWh": 0.25,
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
        /// Annual electricity consumption of the heat pump in kWh.
        /// This is subtracted from the gas savings to get the net saving.
        /// </summary>
        public double AnnualElectricityConsumptionKWh { get; set; } = 0.0;

        /// <summary>
        /// Average electricity price paid per kWh (incl. taxes).
        /// When set to 0, the price is calculated automatically from measured data.
        /// </summary>
        public double ElectricityPriceEurPerKWh { get; set; } = 0.0;

        /// <summary>
        /// Effective electricity price — uses configured value or falls back to measured average.
        /// </summary>
        public double EffectiveElectricityPriceEurPerKWh { get; set; } = 0.0;

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
        /// Annual electricity cost of running the heat pump.
        /// </summary>
        public double AnnualElectricityCostEur =>
            AnnualElectricityConsumptionKWh * EffectiveElectricityPriceEurPerKWh;

        /// <summary>
        /// Total annual net savings: gas savings + standing charge - electricity cost.
        /// </summary>
        public double TotalAnnualSavingsEur =>
            AnnualGasCostSavedEur + GasStandingChargeEurPerYear - AnnualElectricityCostEur;

        /// <summary>
        /// Monthly net savings.
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