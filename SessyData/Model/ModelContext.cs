using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace SessyData.Model
{
    public class ModelContext : DbContext
    {
        public DbSet<SolarHistory> SolarHistory { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite("Data Source=Sessy.db");
    }

    public class SolarHistory
    {
        [Key]
        public DateTime Time { get; set; }
        public double GlobalRadiation { get; set; }
        public double GeneratedPower { get; set; }
    }
}

