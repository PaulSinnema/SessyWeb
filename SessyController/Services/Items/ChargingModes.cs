namespace SessyController.Services.Items
{
    public class ChargingModes
    {
        public enum Modes
        {
            Unknown,
            Charging,
            Discharging,
            ZeroNetHome,
            Disabled
        };

        public string GetDisplayMode(Modes mode)
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

        public Modes GetMode(QuarterlyInfo qi)
        {
            if (qi.Charging) return Modes.Charging;
            if (qi.Discharging) return Modes.Discharging;
            if (qi.ZeroNetHome) return Modes.ZeroNetHome;
            if (qi.Disabled) return Modes.Disabled;
            return Modes.Unknown;
        }
    }
}
