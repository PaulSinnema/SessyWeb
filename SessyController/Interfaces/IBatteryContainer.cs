namespace SessyController.Services.Items
{
    /// <summary>
    /// Provides access to battery capacity information.
    /// Abstracted as an interface to enable unit testing without hardware dependencies.
    /// </summary>
    public interface IBatteryContainer
    {
        /// <summary>
        /// Returns the total capacity of all batteries in Wh.
        /// </summary>
        double GetTotalCapacity();
    }
}