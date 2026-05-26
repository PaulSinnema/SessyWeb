namespace SessyController.Services
{
    /// <summary>
    /// Shared battery threshold constants used across multiple services.
    /// Single source of truth — change here, takes effect everywhere.
    /// </summary>
    public static class BatteryConstants
    {
        /// <summary>
        /// SOC ratio at which the battery is considered full (99.5%).
        /// Used by EnergySystemInput, HardwareStatusService, MilpService and BatteriesService.
        /// </summary>
        public const double FullThresholdRatio = 0.995;

        /// <summary>
        /// Minimum charging current in Watts below which the battery is
        /// considered NOT to be charging. Negative = charging direction.
        /// Covers sensor noise and autonomous NZH trickle current.
        /// </summary>
        public const double ChargingThresholdW = -50.0;
    }
}