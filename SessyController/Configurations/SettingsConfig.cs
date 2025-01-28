namespace SessyController.Configurations
{
    public class SettingsConfig
    {
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
        /// How much is needed for the home (this will have to be estimated later).
        /// </summary>
        public double RequiredHomeEnergy { get; set; }

        public int NumberOfSolarPanels { get; set; }
        public double PeakPowerPerPanel { get; set; }

    }
}
