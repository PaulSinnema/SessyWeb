using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal;

namespace SessyData.Model
{
    public partial class ModelContext : DbContext
    {
        private string? _connectionString { get; set; }

        public ModelContext(DbContextOptions<ModelContext> options) : base (options)
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

        public DbSet<SolarInverterData> SolarInverterData => Set<SolarInverterData>();

        public DbSet<SessyWebControl> SessyWebControl => Set<SessyWebControl>();

        public DbSet<Consumption> Consumption => Set<Consumption>();

        public DbSet<Performance> Performance => Set<Performance>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite(_connectionString);
    }
}

