using SessyCommon.Enums;

namespace SessyController.Services.Items
{
    public static class ChargingModes
    {
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
    }
}