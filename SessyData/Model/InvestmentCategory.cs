namespace SessyData.Model
{
    /// <summary>
    /// Category of an energy system investment.
    /// Used to determine which savings calculation applies and how investments
    /// are grouped in ROI calculations.
    /// </summary>
    public enum InvestmentCategory
    {
        /// <summary>Solar panel installation (e.g. SolarEdge, SMA, Fronius).</summary>
        Solar = 0,

        /// <summary>Battery storage system (e.g. Sessy, Powerwall, BYD).</summary>
        Storage = 1,

        /// <summary>Heat pump for space heating (e.g. Daikin Altherma, Vaillant, Nibe).</summary>
        HeatPump = 2,

        /// <summary>Air conditioning or heat pump for cooling.</summary>
        AirConditioning = 3,

        /// <summary>EV charger installation.</summary>
        EvCharger = 4,

        /// <summary>Home insulation (roof, walls, floor, windows).</summary>
        Insulation = 5,

        /// <summary>Smart home energy management system or monitoring hardware.</summary>
        EnergyManagement = 6,

        /// <summary>Other investment not covered by above categories.</summary>
        Other = 99
    }
}