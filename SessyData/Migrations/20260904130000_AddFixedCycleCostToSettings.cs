using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SessyData.Model;

#nullable disable

namespace SessyData.Migrations
{
    /// <summary>
    /// Adds a manual cycle-cost override to Settings: UseCalculatedCycleCost (default true = keep the
    /// investment-derived wear cost) and FixedCycleCostEurPerKWh (default 0, used when the checkbox is
    /// off). Hand-written migration; the target model lives in ModelContextModelSnapshot.
    /// </summary>
    [DbContext(typeof(ModelContext))]
    [Migration("20260904130000_AddFixedCycleCostToSettings")]
    public partial class AddFixedCycleCostToSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseCalculatedCycleCost",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "FixedCycleCostEurPerKWh",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.04);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseCalculatedCycleCost",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "FixedCycleCostEurPerKWh",
                table: "Settings");
        }
    }
}
