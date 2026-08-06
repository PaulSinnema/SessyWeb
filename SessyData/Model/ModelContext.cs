using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SessyCommon.Services;
using SessyData.Helpers;

namespace SessyData.Model
{
    public partial class ModelContext : DbContext, IDataProtectionKeyContext
    {
        private string? _connectionString { get; set; }

        public ModelContext(DbContextOptions<ModelContext> options) : base(options)
        {
#pragma warning disable EF1001 // Internal EF Core API usage.
            var extension = options.FindExtension<SqliteOptionsExtension>();
#pragma warning restore EF1001 // Internal EF Core API usage.

            if (extension == null)
            {
                throw new InvalidOperationException($"Connection string not found");
            }

            _connectionString = DockerService.ConnectionString(extension.ConnectionString);
        }

        public DbSet<SessyStatusHistory> SessyStatusHistory => Set<SessyStatusHistory>();

        public DbSet<EnergyHistory> EnergyHistory => Set<EnergyHistory>();

        public DbSet<SolarData> SolarData => Set<SolarData>();

        public DbSet<EPEXPrices> EPEXPrices => Set<EPEXPrices>();

        public DbSet<Taxes> Taxes => Set<Taxes>();

        public DbSet<SessyWebControl> SessyWebControl => Set<SessyWebControl>();

        public DbSet<Consumption> Consumption => Set<Consumption>();

        public DbSet<QuarterlyMeasurement> QuarterlyMeasurements => Set<QuarterlyMeasurement>();

        public DbSet<InverterMeasurement> InverterMeasurements => Set<InverterMeasurement>();

        public DbSet<Investment> Investment => Set<Investment>();
        public DbSet<InvestmentGroup> InvestmentGroups => Set<InvestmentGroup>();

        public DbSet<Settings> Settings => Set<Settings>();

        public DbSet<GasPrice> GasPrices => Set<GasPrice>();

        public DbSet<PlannedAction> PlannedActions => Set<PlannedAction>();

        public DbSet<PlannedQuarter> PlannedQuarters => Set<PlannedQuarter>();

        public DbSet<ActualQuarter> ActualQuarters => Set<ActualQuarter>();

        public DbSet<ForecastSnapshot> ForecastSnapshots => Set<ForecastSnapshot>();

        public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

        public DbSet<AppVersion> AppVersions => Set<AppVersion>();

        /// <summary>
        /// Applied to every connection this context opens. Session settings only — they configure
        /// the connection and never write to the file.
        ///
        /// synchronous=NORMAL halves the fsyncs per commit and is the recommended companion to WAL:
        /// a crash can lose the last transaction, never the database. busy_timeout replaces an
        /// immediate "database is locked" with a short wait.
        ///
        /// journal_mode is set once at startup instead — it is stored in the file, so setting it is
        /// a write. See SqliteSetup.EnableWriteAheadLogging.
        /// </summary>
        private const string ConnectionPragmas =
            "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options
                .UseSqlite(_connectionString)
                .AddInterceptors(new SqlitePragmaInterceptor(ConnectionPragmas));
            //.LogTo(log =>
            //{
            //    if (log.Contains("EPEXPrices", StringComparison.OrdinalIgnoreCase) &&
            //        log.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            //    {
            //        Console.WriteLine(log);
            //    }
            //})  // log SQL
            //.EnableSensitiveDataLogging();                   // ook parameterwaarden
        }
    }
}