using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// Drops the settings of the calculated night reserve, removed in v1.0.160 and unread since:
    /// UseCalculatedNightReserve, NightReserveCapPct and ReserveSafetyFactor. SQLite 3.35+ drops a
    /// column in place, so no table rebuild is needed.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20261122160000_RemoveNightReserveSettings")]
    public partial class RemoveNightReserveSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Settings\" DROP COLUMN \"UseCalculatedNightReserve\";");
            migrationBuilder.Sql("ALTER TABLE \"Settings\" DROP COLUMN \"NightReserveCapPct\";");
            migrationBuilder.Sql("ALTER TABLE \"Settings\" DROP COLUMN \"ReserveSafetyFactor\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "UseCalculatedNightReserve", table: "Settings", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<double>(name: "NightReserveCapPct", table: "Settings", type: "REAL", nullable: false, defaultValue: 0.0);
            migrationBuilder.AddColumn<double>(name: "ReserveSafetyFactor", table: "Settings", type: "REAL", nullable: false, defaultValue: 1.10);
        }
    }
}
