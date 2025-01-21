namespace SessyController.Interfaces
{
    public interface ISolarSystemInterface
    {
        Task<short> GetACPower();
        Task<short> GetACPowerScaleFactor();
        Task<short> GetStatus();
    }
}