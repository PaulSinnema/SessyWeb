using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

            _connectionString = extension.ConnectionString;
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

        public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options
                .UseSqlite(_connectionString);
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