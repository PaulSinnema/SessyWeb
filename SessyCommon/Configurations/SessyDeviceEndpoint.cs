namespace SessyCommon.Configurations
{
    /// <summary>Address and credentials of a Sessy device (battery or P1 meter).</summary>
    public class SessyDeviceEndpoint
    {
        /// <summary>A custom name for the device.</summary>
        public string? Name { get; set; }

        /// <summary>Userid from the installation card.</summary>
        public string? UserId { get; set; }

        /// <summary>Password from the installation card.</summary>
        public string? Password { get; set; }

        /// <summary>Base url where the API of the device can be found.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>
        /// True when this entry describes a real device. Configuration merges per key across
        /// appsettings.json and secrets.json, so credentials left behind in secrets.json for a
        /// device that was removed from appsettings.json otherwise conjure up a device with no
        /// address at all — which then fails on every poll with "Value cannot be null (BaseUrl)".
        /// Secrets augment a declared device; they do not declare one.
        /// </summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
    }
}
