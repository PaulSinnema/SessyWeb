namespace SessyCommon
{
    /// <summary>
    /// Single source of the application version — bump the patch here on every change.
    ///
    /// It used to be a literal in MainLayout.razor, which the UI could show but nothing else
    /// could read. Program.cs now writes this value into the AppVersions table at startup, so a
    /// database file always records which builds have run against it.
    /// </summary>
    public static class AppInfo
    {
        public const string Version = "v1.0.74";
    }
}
