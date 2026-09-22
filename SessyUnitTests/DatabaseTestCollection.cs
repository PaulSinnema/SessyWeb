using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Serialises the tests that create a fresh SQLite database via Migrate(). Run in parallel with
    /// each other they flood the process with concurrent database-file creations, which SQLite
    /// reports as "unable to open database file". Sharing one collection runs them one at a time.
    /// </summary>
    [CollectionDefinition("Database", DisableParallelization = true)]
    public class DatabaseTestCollection
    {
    }
}
