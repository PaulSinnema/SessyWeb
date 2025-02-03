using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal;
using System.ComponentModel.DataAnnotations;

namespace SessyData.Model
{
    public class ModelContext : DbContext
    {
        private string? _connectionString { get; set; }

        public ModelContext(DbContextOptions options) : base (options)
        {
            var extension = options.FindExtension<SqliteOptionsExtension>();

            if(extension == null)
            {
                throw new InvalidOperationException($"Connection string not found");
            }

            _connectionString = extension.ConnectionString;
        }

        public DbSet<SolarHistory> SolarHistory { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite(_connectionString);
    }

    public class SolarHistory
    {
        [Key]
        public DateTime Time { get; set; }
        public double GlobalRadiation { get; set; }
        public double GeneratedPower { get; set; }
    }
}

