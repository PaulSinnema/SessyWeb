namespace SessyController.Services.StateMachine
{
    /// <summary>
    /// Describes the active inverter curtailment mode.
    /// Used by EnergySystemAction (output) and displayed in the UI header.
    /// </summary>
    public enum CurtailmentMode
    {
        /// <summary>No curtailment — inverter runs at full output.</summary>
        None,

        /// <summary>
        /// Selling price negative, battery charging.
        /// Inverter throttled to consumption + charging so nothing is exported.
        /// </summary>
        ZeroExport,

        /// <summary>
        /// Selling price negative, battery full.
        /// Inverter throttled to consumption only.
        /// </summary>
        Throttle,

        /// <summary>
        /// Selling price negative, battery not full and not charging.
        /// Inverter set to 0W.
        /// </summary>
        Shutdown
    }
}