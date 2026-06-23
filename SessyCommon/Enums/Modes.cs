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
        Disabled = 4
    }
}