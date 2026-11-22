namespace SessyCommon
{
    /// <summary>Energy units for quarter-hour data: one place for the W-per-quarter to kWh conversion.</summary>
    public static class Units
    {
        /// <summary>Length of a planning quarter in hours.</summary>
        public const double QuarterHours = 0.25;

        /// <summary>Average watts over one quarter, as kWh.</summary>
        public static double QuarterKWh(double watts) => watts * QuarterHours / 1000.0;

        /// <summary>Energy into the battery this quarter; battery power is negative while charging.</summary>
        public static double ChargedKWh(double batteryPowerW) => batteryPowerW < 0 ? QuarterKWh(-batteryPowerW) : 0.0;

        /// <summary>Energy out of the battery this quarter; battery power is positive while discharging.</summary>
        public static double DischargedKWh(double batteryPowerW) => batteryPowerW > 0 ? QuarterKWh(batteryPowerW) : 0.0;
    }
}
