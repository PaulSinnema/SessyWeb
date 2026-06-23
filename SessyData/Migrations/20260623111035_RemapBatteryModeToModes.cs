using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RemapBatteryModeToModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GlobalRadiation",
                table: "QuarterlyMeasurements");

            migrationBuilder.DropColumn(
                name: "GridExportWh",
                table: "QuarterlyMeasurements");

            migrationBuilder.DropColumn(
                name: "GridImportWh",
                table: "QuarterlyMeasurements");

            // Old Disabled (0) becomes new Disabled (4).
            migrationBuilder.Sql(
                "UPDATE QuarterlyMeasurements SET BatteryMode = 4 WHERE BatteryMode = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "GlobalRadiation",
                table: "QuarterlyMeasurements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "GridExportWh",
                table: "QuarterlyMeasurements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "GridImportWh",
                table: "QuarterlyMeasurements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            // Reverse: new Disabled (4) back to old Disabled (0).
            migrationBuilder.Sql(
                "UPDATE QuarterlyMeasurements SET BatteryMode = 0 WHERE BatteryMode = 4;");
        }
    }
}
