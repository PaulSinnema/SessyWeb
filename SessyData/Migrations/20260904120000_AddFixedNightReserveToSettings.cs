using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// Adds a manual night-reserve override to Settings: UseCalculatedNightReserve (default true =
    /// keep the self-learned reserve) and FixedNightReservePct (default 10%, used when the checkbox
    /// is off). Hand-written migration; the target model lives in ModelContextModelSnapshot.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20260904120000_AddFixedNightReserveToSettings")]
    public partial class AddFixedNightReserveToSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseCalculatedNightReserve",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<double>(
                name: "FixedNightReservePct",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 10.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseCalculatedNightReserve",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "FixedNightReservePct",
                table: "Settings");
        }
    }
}
