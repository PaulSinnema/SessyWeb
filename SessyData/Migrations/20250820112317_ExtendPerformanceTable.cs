using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class ExtendPerformanceTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ChargeLeftPercentage",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "DisplayState",
                table: "Performance",
                type: "TEXT",
                nullable: true);

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
                name: "SolarGlobalRadiation",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SolarPowerPerQuarterHour",
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

            migrationBuilder.AddColumn<double>(
                name: "VisualizeInChart",
                table: "Performance",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargeLeftPercentage",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "DisplayState",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "EstimatedConsumptionPerQuarterHourVisual",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "ProfitVisual",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "SolarGlobalRadiation",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "SolarPowerPerQuarterHour",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "SolarPowerVisual",
                table: "Performance");

            migrationBuilder.DropColumn(
                name: "VisualizeInChart",
                table: "Performance");
        }
    }
}
