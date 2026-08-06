using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data;
using System.Data.Common;

namespace SessyData.Helpers
{
    /// <summary>
    /// Applies the per-connection SQLite pragmas. Both of these are session settings that cost
    /// nothing and cannot fail: they change how this connection behaves, they do not touch the
    /// database file.
    ///
    /// journal_mode deliberately does NOT live here. It is stored in the database file itself, so
    /// setting it is a WRITE, and a write needs an exclusive lock on a database nothing else is
    /// using. Running it on every connection open made every open a potential failure — on the
    /// development machine it threw "SQLite Error 8: attempt to write a readonly database" on the
    /// very first connection, before the migrations had even run. It is a one-time, database-wide
    /// setting, so it belongs at startup: see <see cref="SqliteSetup.EnableWriteAheadLogging"/>.
    /// </summary>
    public class SqlitePragmaInterceptor : DbConnectionInterceptor
    {
        private readonly string _pragmas;

        public SqlitePragmaInterceptor(string pragmas)
        {
            _pragmas = pragmas;
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            Apply(connection);

            base.ConnectionOpened(connection, eventData);
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Apply(connection);

            await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Never lets a pragma take the connection down with it: these are tuning settings, and the
        /// application is perfectly able to run on the defaults.
        /// </summary>
        private void Apply(DbConnection connection)
        {
            try
            {
                using var command = connection.CreateCommand();

                command.CommandText = _pragmas;
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not apply SQLite pragmas ({_pragmas}): {ex.Message}");
            }
        }
    }

    /// <summary>One-time database-wide SQLite settings, applied at startup.</summary>
    public static class SqliteSetup
    {
        /// <summary>
        /// Switches the database to write-ahead logging and reports the mode it ended up in.
        ///
        /// This is the setting that matters for a responsive UI: in the default journal mode a
        /// writer takes an exclusive lock on the whole file, so every reader — every page in the
        /// UI — waits for the commit. Under WAL readers never block behind a writer.
        ///
        /// It is persistent, so it only has to succeed once. Failure is not fatal: the application
        /// runs on the default mode, just with readers queueing behind writers, and the reason ends
        /// up in the log rather than in a crash on startup.
        /// </summary>
        public static string EnableWriteAheadLogging(DbConnection connection)
        {
            try
            {
                if (connection.State != ConnectionState.Open)
                    connection.Open();

                using var command = connection.CreateCommand();

                command.CommandText = "PRAGMA journal_mode=WAL;";

                var mode = command.ExecuteScalar() as string ?? "unknown";

                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"SQLite stayed in '{mode}' journal mode ({connection.DataSource}) — readers will queue behind writers.");
                else
                    Console.WriteLine($"SQLite journal mode: {mode}");

                return mode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not switch SQLite to WAL ({connection.DataSource}): {ex.Message}");

                return "unknown";
            }
        }
    }
}
