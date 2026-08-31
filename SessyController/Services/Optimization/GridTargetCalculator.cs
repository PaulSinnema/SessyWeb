using SessyCommon.Enums;

namespace SessyController.Services.Optimization
{
    /// <summary>
    /// Pure conversion from a wanted battery (dis)charge power to a P1 grid target.
    /// In NOM the Sessy holds net = grid_target, so battery = houseNet - grid_target.
    /// Signs: P1 net import +, export -; battery discharge +, charge -; grid target import +, export -.
    /// </summary>
    public static class GridTargetCalculator
    {
        /// <summary>House net load (consumption - solar) = live P1 net + battery power.</summary>
        public static double HouseNetW(double p1NetW, double batteryPowerW)
            => p1NetW + batteryPowerW;

        /// <summary>
        /// Grid target in watts for a mode and wanted power P.
        /// Charging: houseNet + P; Discharging: houseNet - P; ZeroNetHome: 0.
        /// P is clamped to [0, nameplate] so the implied battery power never exceeds the bank.
        /// Disabled returns 0 but runs outside NOM (API + setpoint 0), so callers ignore it.
        /// </summary>
        public static int GridTargetW(Modes mode, double houseNetW, double powerW,
                                      double maxChargeW, double maxDischargeW)
        {
            switch (mode)
            {
                case Modes.Charging:
                    return (int)Math.Round(houseNetW + ClampPower(powerW, maxChargeW));
                case Modes.Discharging:
                    return (int)Math.Round(houseNetW - ClampPower(powerW, maxDischargeW));
                case Modes.ZeroNetHome:
                    return 0;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Last-resort clamp on the target: bound it so the implied battery power
        /// (houseNet - target) stays within [-maxCharge, +maxDischarge]. Guards the batteries
        /// against a bad target even if the Sessy misbehaves.
        /// </summary>
        public static int SafeClamp(int gridTargetW, double houseNetW,
                                    double maxChargeW, double maxDischargeW)
        {
            var lo = houseNetW - Math.Max(0.0, maxDischargeW);
            var hi = houseNetW + Math.Max(0.0, maxChargeW);
            return (int)Math.Round(Math.Clamp((double)gridTargetW, lo, hi));
        }

        // Never below 0, never above the nameplate maximum.
        private static double ClampPower(double powerW, double maxW)
            => Math.Clamp(powerW, 0.0, maxW <= 0 ? 0.0 : maxW);
    }
}
