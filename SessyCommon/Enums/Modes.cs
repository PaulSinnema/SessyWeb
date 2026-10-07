namespace SessyCommon.Enums
{
    /// <summary>
    /// Battery operating mode. Lives in SessyCommon so both the data layer (stored on
    /// QuarterlyMeasurement) and the controller layer (planner/state machine) can use the
    /// same type without a circular project reference. Integer values are fixed for stable
    /// persistence — do not reorder.
    /// </summary>
    public enum Modes
    {
        Unknown = 0,
        Charging = 1,
        Discharging = 2,
        ZeroNetHome = 3,
        Disabled = 4,

        /// <summary>
        /// Store solar surplus, never discharge: runs on NOM with a P1 target that follows the live
        /// net load, so surplus &gt; 0 charges the battery and surplus &lt;= 0 leaves it idle. Planned
        /// once the reserve is reached, so a forecast error cannot drain the battery below it.
        /// </summary>
        SolarOnly = 5
    }
}