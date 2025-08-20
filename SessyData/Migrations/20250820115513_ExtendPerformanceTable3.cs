using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class ExtendPerformanceTable3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargeLeftVisual",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "ChargeNeededVisual",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "EstimatedConsumptionPerQuarterHourVisual",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "ProfitVisual",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "SolarPowerVisual",
                table: "Performance");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ChargeLeftVisual",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ChargeNeededVisual",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "EstimatedConsumptionPerQuarterHourVisual",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ProfitVisual",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SolarPowerVisual",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
