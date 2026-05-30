using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRedundancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SolarProductionKWh",
                table: "QuarterlyMeasurements");

            migrationBuilder.DropColumn(
                name: "GlobalRadiation",
                table: "EnergyHistory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SolarProductionKWh",
                table: "QuarterlyMeasurements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "GlobalRadiation",
                table: "EnergyHistory",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
