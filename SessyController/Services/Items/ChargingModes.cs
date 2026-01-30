using SessyData.Model;

namespace SessyController.Services.Items
{
    public static class ChargingModes
    {
        public enum Modes
        {
            Unknown,
            Charging,
            Discharging,
            ZeroNetHome,
            Disabled
        };

        public static string GetDisplayMode(Modes mode)
        {
            switch (mode)
            {
                case Modes.Unknown:
                    return "?";
                case Modes.Charging:
                    return "Charging";
                case Modes.Discharging:
                    return "Discharging";
                case Modes.ZeroNetHome:
                    return "Zero net home";
                case Modes.Disabled:
                    return "Disabled";
                default:
                    return "?";
            }
        }

        public static Modes GetMode(QuarterlyInfo quarterlyInfo)
        {
            if (quarterlyInfo.Charging) return Modes.Charging;
            if (quarterlyInfo.Discharging) return Modes.Discharging;
            if (quarterlyInfo.ZeroNetHome) return Modes.ZeroNetHome;
            if (quarterlyInfo.Disabled) return Modes.Disabled;
            return Modes.Unknown;
        }

        public static Modes GetMode(Performance performance)
        {
            if (performance.Charging) return Modes.Charging;
            if (performance.Discharging) return Modes.Discharging;
            if (performance.ZeroNetHome) return Modes.ZeroNetHome;
            if (performance.Disabled) return Modes.Disabled;
            return Modes.Unknown;
        }
    }
}
