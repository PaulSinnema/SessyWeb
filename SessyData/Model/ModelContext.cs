using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal;

namespace SessyData.Model
{
    public partial class ModelContext : DbContext
    {
        private string? _connectionString { get; set; }

        public ModelContext(DbContextOptions<ModelContext> options) : base (options)
        {
            var extension = options.FindExtension<SqliteOptionsExtension>();

            if(extension == null)
            {
                throw new InvalidOperationException($"Connection string not found");
            }

            _connectionString = extension.ConnectionString;
        }

        //protected override void OnModelCreating(ModelBuilder modelBuilder)
        //{
        //    modelBuilder.Entity<SolarHistory>().ToTable("SolarHistory");
        //    modelBuilder.Entity<SessyStatusHistory>().ToTable("SessyStatusHistory");
        //}

        public DbSet<SessyStatusHistory> SessyStatusHistory => Set<SessyStatusHistory>();

        public DbSet<SolarHistory> SolarHistory => Set<SolarHistory>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite(_connectionString);
    }
}

