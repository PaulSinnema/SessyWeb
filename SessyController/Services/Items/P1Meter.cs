using SessyCommon.Configurations;

namespace SessyController.Services.Items
{
    /// <summary>A configured P1 meter and its key in appsettings.json.</summary>
    public class P1Meter : SessyDeviceEndpoint
    {
        public string? Id { get; internal set; }
    }
}
